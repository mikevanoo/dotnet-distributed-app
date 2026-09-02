# Kubernetes deployment: testing commands

Commands for deploying the app to a local Kubernetes cluster (Rancher Desktop) and verifying it
end to end. See `docs/DEPLOYMENT-PLAN.md` for why the deployment is shaped the way it is.

Everything here targets the local cluster. On AKS the app URLs change but the `kubectl` commands do
not.

All commands are PowerShell 7 (`pwsh`). Note that `curl` is not an alias for `Invoke-WebRequest` in
PowerShell 7, so the native cmdlets are used throughout.

## Most of this is automated

`tests/DotNetDistributedApp.DeploymentTests` runs almost everything below as assertions. Deploy, then:

```powershell
./deployment-test.ps1                # chart + cluster
./deployment-test.ps1 -ChartOnly     # renders the chart and asserts on it; no cluster needed
```

The tests are opt-in - a plain `dotnet test` discovers and skips them, so they never run in CI - and
the cluster tier refuses to start unless `kubectl config current-context` matches
`DeploymentTests__ExpectedKubeContext` (default `rancher-desktop`). Point them elsewhere with
`DeploymentTests__BaseUrl` and friends; that is the whole of what changes for AKS.

Prefer the tests for the checks they cover, and keep this page for the parts they cannot replace:
deploying in the first place, teardown, driving MCP from a real client, and any hands-on exploration
of a deployment that is misbehaving. Where a section is fully automated it says which test class
covers it.

---

## Before you start

```powershell
# The deploy targets whatever context is current, and there is no --context flag. Check it.
kubectl config current-context          # expect: rancher-desktop
kubectl config use-context rancher-desktop
```

`aspire deploy` pushes images even though k3s here shares a container runtime with the host and could
resolve them locally, so a registry has to be running:

```powershell
docker run -d --restart=unless-stopped -p 5000:5000 --name aspire-registry registry:3
(Invoke-WebRequest http://localhost:5000/v2/ -SkipHttpErrorCheck).StatusCode   # expect 200
```

Prerequisites: Rancher Desktop running with Kubernetes enabled, Helm 4.2.0+, and a reachable Docker
daemon (`docker info`). If `docker` times out on a Hyper-V socket, the VM is still starting - wait and
retry rather than assuming it is broken.

---

## Deploy

```powershell
# Chart only, no cluster contact - the fastest way to check what will be applied
aspire publish -o .aspire-publish
helm lint .aspire-publish
helm template dotnet-distributed-app .aspire-publish > rendered.yaml

# Validate against the live API server without persisting anything
kubectl apply --dry-run=server -f rendered.yaml

# Build images, push to the local registry, install the chart
aspire deploy --non-interactive --nologo -o .aspire-deploy

# List the pipeline steps without running them
aspire deploy --list-steps --non-interactive
```

Namespace and release are both `dotnet-distributed-app`.

---

## Check the deployment came up

```powershell
$N = 'dotnet-distributed-app'

kubectl get pods -n $N
kubectl get svc,ingress,pvc -n $N
```

Automated by `Cluster/DeployedWorkloadsShould` and `Cluster/MigrationJobLogShould`.

Expected: every pod `Running` **except** `api-database-migrations-job-*`, which must be `Completed`
with **0 restarts**. If it shows restarts it has been published as a Deployment rather than a Job -
see `PublishAsKubernetesJob` in `src/DotNetDistributedApp.AppHost/KubernetesBuilderExtensions.cs`.

```powershell
# Migration output - the only place a failed migration surfaces, as this service has no telemetry
kubectl logs -n $N job/api-database-migrations-job

# Follow a service
kubectl logs -n $N deploy/api-deployment -f
kubectl logs -n $N deploy/events-consumer-deployment --tail=100

# Confirm service discovery wiring
kubectl exec -n $N deploy/api-deployment -- printenv | Select-String '^services__'
```

The last one should list an entry per referenced service, for example
`services__spatial-api__http__0=http://spatial-api-service:8080`. Only **http** endpoints are
registered in the chart, which is why clients have to use `https+http://` base addresses.

---

## API endpoints

Base URL is `http://localhost` via Traefik. The Ingress has a default backend and no host rule, so
any hostname works. `192.168.127.2` (the Traefik LoadBalancer address) is VM-internal and **not**
reachable from Windows.

Only `api` is routed. `spatial-api`, `mcp-server` and the Aspire dashboard are `ClusterIP` - see
port-forwarding below.

### Health

Automated by `Api/HealthEndpointsShould`.

```powershell
(Invoke-WebRequest http://localhost/health -SkipHttpErrorCheck).StatusCode   # all checks
(Invoke-WebRequest http://localhost/alive  -SkipHttpErrorCheck).StatusCode   # liveness only
```

Both return 200 in every environment. They used to be Development-only, which meant probes would
404 against a deployed pod.

### Weather

Automated by `Api/WeatherEndpointsShould` and `Api/DeployedDependencyChainShould`.

Seeded station keys: `heathrow`, `stornoway`.

```powershell
Invoke-RestMethod http://localhost/v1/weather/stations | ConvertTo-Json -Depth 6
Invoke-RestMethod http://localhost/v2/weather/stations | ConvertTo-Json -Depth 6
Invoke-RestMethod http://localhost/v2/weather/more-stations

Invoke-RestMethod http://localhost/v1/weather/stations/heathrow/historic-data | ConvertTo-Json -Depth 6
Invoke-RestMethod 'http://localhost/v1/weather/stations/heathrow/historic-data?fromYear=1950&toYear=1960' | ConvertTo-Json -Depth 6
```

`/stations` exercises Postgres, the cache, `spatial-api` and `geoip-api` in a single call. **Check the
payload, not the status code** - the resilience fallbacks return 204 on an outage and the API degrades
to nulls rather than failing, so a broken dependency still yields HTTP 200. This is the one command
worth knowing, because it shows all three signals at once:

```powershell
$elapsed = Measure-Command { $r = Invoke-RestMethod http://localhost/v1/weather/stations }
"elapsed: $([math]::Round($elapsed.TotalSeconds, 2))s"
$r.response | Format-Table key, latitude, longitude, easting, northing
"geoData country: $($r.metadata.geoData.country)"
```

A healthy result looks like this - sub-second, with all columns populated:

```
elapsed: 0.09s

key       latitude longitude   easting  northing
---       -------- ---------   -------  --------
heathrow     51.48     -0.45 507805.48 176700.57
stornoway    58.21     -6.32 146489.83 933154.87

geoData country: US
```

- `easting` / `northing` empty → `spatial-api` unreachable (they are nullable, so they silently vanish)
- `geoData country` empty → `geoip-api` unreachable
- seconds rather than milliseconds → a dependency is timing out and burning its retry budget

`historic-data` is output-cached for 30s, so the second call should be markedly faster. A firmer
check than the clock: the envelope's `metadata.timestamp` is stamped when the response is built, so
two calls within the window come back **byte-identical**. That is what
`Api/WeatherEndpointsShould` asserts, timing being far too noisy on a local cluster.

### Events

Automated by `Api/EventEndpointsShould`, `Events/TransactionalInboxShould` and
`Events/EventRetryLadderShould`. The tests read the inbox with Npgsql over a port forward rather than
`psql` in the pod, and assert on count deltas: the API generates the event id and partition key
server-side, so nothing in the HTTP response identifies the row a call will produce.

```powershell
Invoke-RestMethod http://localhost/v1/events/simple-event -Method Post -ContentType 'application/json' -Body '{"value":"hello"}'

# no body; exercises the retry ladder and the dead letter queue
Invoke-RestMethod http://localhost/v1/events/failing-event -Method Post

# sends the same payload twice, so the inbox skips the second
Invoke-RestMethod http://localhost/v1/events/duplicate-event -Method Post -ContentType 'application/json' -Body '{"value":"dupe"}'
```

All three return 200 immediately and produce no output - they only publish to Kafka. Verify the
consumer side:

```powershell
kubectl logs -n $N deploy/events-consumer-deployment --tail=100 |
    Select-String -Pattern 'handling|duplicate|dead letter|failed'
```

Expected: `Handling simple event` for `simple-event`; `Duplicate event skipped` for the second copy
from `duplicate-event`; and `failing-event` climbing through `attempt 1/4` … `4/4` before being dead
lettered.

Inspect the transactional inbox directly:

```powershell
$pw = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String(
        (kubectl get secret -n $N api-database-server-secrets -o jsonpath='{.data.POSTGRES_PASSWORD}')))

kubectl exec -n $N api-database-server-statefulset-0 -- env PGPASSWORD="$pw" psql -U postgres -d api-database -c "select event_name, count(*) from processed_weather_events group by event_name;"
```

`failing-event` should be **absent** - a dead-lettered event is never recorded as processed.

Note `ProcessedWeatherEventsCleaner` in `scheduled-tasks` runs every minute and deletes across all
consumer groups, so rows older than the configured retention disappear while you are testing.

---

## Services without ingress

`kubectl port-forward` blocks, so start it as a background process and stop it when done. The
left-hand port is the local one and is arbitrary - except for the dashboard, where matching it to
18888 makes the login URL the pod prints usable as-is.

### spatial-api

Automated by `SpatialApi/DeployedCoordinateConverterShould`.

```powershell
$pf = Start-Process kubectl -PassThru -ArgumentList 'port-forward', '-n', $N, 'svc/spatial-api-service', '8081:8080'
Start-Sleep -Seconds 3

Invoke-RestMethod 'http://localhost:8081/v1/coordinate-converter/to-os-national-grid-reference?latitude=51.479&longitude=-0.449'
Invoke-RestMethod 'http://localhost:8081/v1/coordinate-converter/to-latitude-longitude?easting=507800&northing=176100'

Stop-Process -Id $pf.Id
```

Note the grid-reference endpoint returns `easting` / `northing` numbers, not a grid-reference string.

### MCP server

Automated by `McpServer/DeployedMcpServerShould`, which drives the real MCP client SDK and so needs
none of the by-hand protocol work below.

No ingress and no UI - it speaks MCP over streamable HTTP at `/mcp`. Start with the health endpoint,
which is plain HTTP and rules out the port-forward before you debug the protocol:

```powershell
$pf = Start-Process kubectl -PassThru -ArgumentList 'port-forward', '-n', $N, 'svc/mcp-server-service', '8082:8080'
Start-Sleep -Seconds 3

(Invoke-WebRequest http://localhost:8082/health -SkipHttpErrorCheck).StatusCode   # expect 200
```

Driving the protocol by hand needs three things that are easy to miss:

- Every request must accept `text/event-stream`. Replies come back as SSE (`data: {...}`), not JSON,
  so `Invoke-RestMethod` hands you a string rather than an object - hence the helper below.
- The `Mcp-Session-Id` header returned by `initialize` has to be echoed on every later request.
  `SessionMode` is `StatefulForInitializeClients`, and the session lives in the pod's memory.
- The handshake is not complete until `notifications/initialized` has been sent.

```powershell
$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pwsh","version":"1.0"}}}'
$r = Invoke-WebRequest http://localhost:8082/mcp -Method Post -ContentType 'application/json' `
    -Headers @{ Accept = 'application/json, text/event-stream' } -Body $init
$h = @{ Accept = 'application/json, text/event-stream'; 'Mcp-Session-Id' = $r.Headers['Mcp-Session-Id'][0] }

# expect 202 and an empty body
(Invoke-WebRequest http://localhost:8082/mcp -Method Post -ContentType 'application/json' -Headers $h `
    -Body '{"jsonrpc":"2.0","method":"notifications/initialized"}').StatusCode

function Invoke-Mcp($body)
{
    $response = Invoke-WebRequest http://localhost:8082/mcp -Method Post -ContentType 'application/json' `
        -Headers $h -Body $body -SkipHttpErrorCheck
    ($response.Content -split "`n" | Where-Object { $_ -like 'data: *' }) -replace '^data: ', '' | ConvertFrom-Json
}
```

`$r.Content` from `initialize` should report a `serverInfo.name` of `DotNetDistributedApp.McpServer`.
Then:

```powershell
(Invoke-Mcp '{"jsonrpc":"2.0","id":2,"method":"tools/list"}').result.tools | Format-Table name, description
```

Expected: exactly three tools - `list_weather_stations`, `summarise_station_historic_data` and
`get_station_historic_data`.

Every tool sets `UseStructuredContent = true`, so read `result.structuredContent` and skip parsing
the text block:

```powershell
$stations = Invoke-Mcp '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_weather_stations","arguments":{}}}'
$stations.result.structuredContent.stations | Format-Table key, displayName, easting, northing

$summary = Invoke-Mcp '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"summarise_station_historic_data","arguments":{"stationKey":"heathrow","fromYear":1950,"toYear":1960}}}'
$summary.result.structuredContent | Format-List
```

A healthy `summarise` result for 1950-1960 has `monthsExpected` and `monthsReturned` both 132.

This is the end-to-end check worth running, because the MCP server holds no data of its own: the
tools call `api` over service discovery (`https+http://api`), so populated `easting` / `northing`
prove the chain from a second pod through `api` to `spatial-api`. If `api` is unreachable the
resilience fallback returns 204 and the tool fails with *"The weather API could not be reached"* -
a readable message rather than a 500.

Two failure shapes are worth provoking:

```powershell
# a validation failure the tool raises itself keeps its detail
(Invoke-Mcp '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_station_historic_data","arguments":{"stationKey":"heathrow","fromYear":1960,"toYear":1950}}}').result.content.text

# a missing required argument
(Invoke-Mcp '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_station_historic_data","arguments":{"stationKey":"heathrow"}}}').result.content.text
```

Both come back prefixed with `An error occurred invoking '<tool>':` - as of
`ModelContextProtocol` 2.2.0 the SDK **prepends** that rather than replacing the message, so the
detail after the colon is still the useful part. `McpServer/DeployedMcpServerShould` asserts on that
detail rather than on the prefix being absent, for exactly that reason.

The second call fails because `fromYear` and `toYear` are nullable but have no default value, so the
generated schema still marks them required.

> **Passing explicit nulls does not work either.** `WeatherApiClient` always appends
> `?fromYear={fromYear}&toYear={toYear}`, so nulls become empty query values and the weather API
> answers 400 - the tool then reports *"The weather API rejected the request with 400 BadRequest"*.
> There is currently **no** way to ask `get_station_historic_data` for an unfiltered range; pass real
> years. Fixing it means omitting the parameters from the query string when they are null.

Whenever a call returns a message you cannot place, the pod log has the real exception:

```powershell
kubectl logs -n $N deploy/mcp-server-deployment --tail=100 | Select-String -Pattern 'threw an unhandled|IsError = True'
```

To drive it from a real client instead, point one at the same forwarded port - the `mcp-inspector`
resource in `AppHost.cs` is `.ExcludeFromManifest()`, so it is not deployed:

```powershell
# then pick "Streamable HTTP" and http://localhost:8082/mcp
npx @modelcontextprotocol/inspector@0.17.5

# or, from Claude Code - writes to your local MCP config, so remove it afterwards
claude mcp add --transport http weather http://localhost:8082/mcp
claude mcp remove weather

Stop-Process -Id $pf.Id
```

### Aspire dashboard

Automated by `Dashboard/AspireDashboardShould`, apart from actually looking at the pages.

The dashboard is deployed as `k8s-dashboard` and is the OTLP collector for the whole release - every
service gets `OTEL_EXPORTER_OTLP_ENDPOINT=http://k8s-dashboard-service:18889`. Three ports: **18888**
UI, **18889** OTLP/gRPC, **18890** OTLP/HTTP.

Browser-token auth is on. The chart sets no environment variables on the container at all, so the
image defaults apply and the token is generated at startup - it **changes on every pod restart**, so
it has to be re-read from the log rather than bookmarked:

```powershell
$pf = Start-Process kubectl -PassThru -ArgumentList 'port-forward', '-n', $N, 'svc/k8s-dashboard-service', '18888:18888'
Start-Sleep -Seconds 3

$loginUrl = (kubectl logs -n $N deploy/k8s-dashboard-deployment |
    Select-String -Pattern 'Login URL:\s+(\S+)').Matches.Groups[1].Value
$loginUrl                 # http://localhost:18888/login?t=<token>
Start-Process $loginUrl    # opens the browser already authenticated
```

The URL says `localhost:18888` because that is the address the pod sees, and it works from the host
only because the port-forward uses the same local port. Forward to a different one and the host and
port need fixing up by hand - the token is a query parameter, so it survives that.

Verify without a browser. Unauthenticated pages redirect to `/login`, and the token URL sets the auth
cookie:

```powershell
# expect 302, Location = /login?returnUrl=%2Ftraces
$anon = Invoke-WebRequest http://localhost:18888/traces -MaximumRedirection 0 -ErrorAction SilentlyContinue
"anon: $($anon.StatusCode) -> $($anon.Headers.Location)"

$login = Invoke-WebRequest $loginUrl -SessionVariable dash
foreach ($page in '/structuredlogs', '/traces', '/metrics')
{
    "$page -> $((Invoke-WebRequest "http://localhost:18888$page" -WebSession $dash -SkipHttpErrorCheck).StatusCode)"
}

Stop-Process -Id $pf.Id
```

Expected: `anon: 302`, then 200 for all three pages.

**The Resources page stays empty, and that is not a fault.** This is the standalone dashboard image
with no resource service to call, so it knows nothing about pods, endpoints or console output - only
the telemetry that arrives over OTLP. Structured logs, Traces and Metrics are the only pages with
data, and services appear there under their `OTEL_SERVICE_NAME` (`api`, `spatial-api`, `mcp-server`,
`events-consumer`, `scheduled-tasks`, `api-database-migrations`). For the resource view, use
`kubectl get pods`.

What it gives you that `kubectl logs` cannot is one trace crossing pods. Call
`http://localhost/v1/weather/stations`, then open Traces: a single trace should span `api`,
`spatial-api`, `geoip-api`, Postgres and Valkey - which is a faster way to find a slow or failing
dependency than reading the payload for missing columns. Post to `/v1/events/simple-event` and the
trace continues into `events-consumer` through Kafka; `/v1/events/failing-event` shows the retry
ladder as repeated consume attempts.

If telemetry is missing, check the receiver before the exporters:

```powershell
$pf = Start-Process kubectl -PassThru -ArgumentList 'port-forward', '-n', $N, 'svc/k8s-dashboard-service', '18890:18890'
Start-Sleep -Seconds 3

# An empty protobuf body is a valid empty export, so 200 means the receiver is up and unsecured
(Invoke-WebRequest http://localhost:18890/v1/traces -Method Post -ContentType 'application/x-protobuf' `
    -Body ([byte[]]@()) -SkipHttpErrorCheck).StatusCode

Stop-Process -Id $pf.Id
```

A 200 there puts the problem on the exporter side - look for OTLP warnings in the sending pod's log.
The dashboard log confirms both halves on startup:

```powershell
kubectl logs -n $N deploy/k8s-dashboard-deployment | Select-String -Pattern 'listening on|OTLP server is unsecured'
```

`OTLP server is unsecured` is expected here: the chart sets no `DASHBOARD__OTLP__AUTHMODE`, so any
pod that can reach the service can write telemetry. Fine on a local cluster, not fine on AKS.

---

## What is not available

`/scalar` and `/openapi/v1.json` return **404**. Both are registered inside an
`app.Environment.IsDevelopment()` block (`src/DotNetDistributedApp.Api/CoreWebApplicationExtensions.cs`),
and deployed pods have no `ASPNETCORE_ENVIRONMENT` set, so they run as Production. Use the commands
above, or set `ASPNETCORE_ENVIRONMENT=Development` on the `api` resource - bearing in mind that also
turns on the developer exception page.

The developer-tools containers are all `.ExcludeFromManifest()` in `AppHost.cs`, so none of them are
in the cluster: pgAdmin, RedisInsight, Kafka UI and the MCP Inspector. Run the inspector locally
against a port-forward (above); for the others, port-forward the backing service and point a local
client at it.

---

## Teardown

```powershell
helm uninstall dotnet-distributed-app -n $N
kubectl delete namespace $N

# The database volume is not owned by the release and survives an uninstall
kubectl delete pvc api-database-data -n $N

docker rm -f aspire-registry
```

A `kubectl port-forward` left running holds a dead connection open; `Get-Process kubectl | Stop-Process`
clears any that were missed.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `requires image push but no container registry is available` | The registry container is not running, or `AddContainerRegistry` is missing from the AppHost. This also fails `prepare-k8s`, which is what resolves secrets - so `values.yaml` keeps empty passwords. |
| `Name or service not known (spatial-api:443)` | A client is using a single-scheme `https://` base address. The chart registers only http endpoints, so it must be `https+http://`, and `AllowedSchemes` in `ServiceDefaults` must include `http` - the allowlist applies *only* to multi-scheme URIs. |
| Requests return 200 but take ~18s | A dependency is unreachable and burning its retry budget before the fallback returns 204. Check `kubectl logs -n $N deploy/api-deployment` for `BrokenCircuitException`. |
| `ImagePullBackOff` | The image tag in `values.yaml` is not in the registry. Re-run `aspire deploy`; check `Invoke-RestMethod http://localhost:5000/v2/_catalog`. |
| Migration pod restarting | It has been published as a Deployment. It should be a Job - see `PublishAsKubernetesJob`. |
| Deploy went to the wrong cluster | `aspire deploy` uses the current context and has no `--context` flag. Check `kubectl config current-context` first. |
| Dashboard login URL is rejected | The pod restarted and generated a new token. Re-read it from the log. |
| Dashboard Resources page is empty | Expected - standalone dashboard, no resource service. Only the telemetry pages carry data. |
| Dashboard reachable but no traces | Check the OTLP receiver with the 18890 probe above. A 200 there means the problem is the sending pod's exporter. |
| MCP requests after `initialize` fail | The `Mcp-Session-Id` header is not being echoed, or the pod restarted and lost the in-memory session. Re-run the handshake. |
| `Select-String` finds nothing in a pod log that obviously contains the text | The services set Serilog's console sink to `AnsiConsoleTheme.Code` with `applyThemeToRedirectedOutput: true`, so every templated parameter in a log message is wrapped in SGR escape sequences. `dead letter topic common-dlq` is really `dead letter topic <esc>common-dlq<esc>`, and `attempt 1/4` is `attempt <esc>1<esc>/<esc>4<esc>`. Match only the literal part of a template, or strip the codes first - `ClusterFixture.GetPodLogAsync` does. |
| MCP reply is an unparseable string | The response is SSE, not JSON. Accept `text/event-stream` and strip the `data: ` prefix. |
| MCP tool returns `An error occurred invoking '<tool>'.` | The SDK replaces the message of every exception except `McpException`. Usually a missing required argument - `fromYear` / `toYear` are nullable but have no default, so pass explicit nulls. The pod log has the real exception. |
