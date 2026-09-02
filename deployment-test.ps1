#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the Kubernetes deployment tests.

.DESCRIPTION
    These tests are opt-in: a plain `dotnet test` discovers them and skips them, so CI stays green
    without a cluster. This script sets the switches that turn them on.

    Two independent tiers:
      Chart   - renders the Helm chart with `aspire publish` + `helm template` and asserts on it.
                Needs the aspire CLI and helm. No cluster.
      Cluster - talks to a deployed release: workload health, the API through the ingress, the event
                path end to end, and the port-forwarded services.

    The cluster tier writes to its target (it publishes Kafka events and adds inbox rows), so it
    refuses to run unless the current kubeconfig context matches DeploymentTests__ExpectedKubeContext
    (default: rancher-desktop). Deploy first - see docs/K8S-DEPLOYMENT-TESTING-COMMANDS.md.

    Point the suite at another environment with environment variables, for example:
      $env:DeploymentTests__BaseUrl = 'https://weather.example.com'
      $env:DeploymentTests__ExpectedKubeContext = 'aks-demo'

.PARAMETER ChartOnly
    Run only the chart tier. No cluster required.

.PARAMETER ClusterOnly
    Run only the cluster tiers.

.EXAMPLE
    ./deployment-test.ps1
    ./deployment-test.ps1 -ChartOnly
#>
[CmdletBinding()]
param
(
    [switch] $ChartOnly,
    [switch] $ClusterOnly
)

$ErrorActionPreference = 'Stop'

if ($ChartOnly -and $ClusterOnly)
{
    throw 'Pass at most one of -ChartOnly and -ClusterOnly.'
}

$env:RUN_CHART_TESTS = if ($ClusterOnly) { $null } else { '1' }
$env:RUN_DEPLOYMENT_TESTS = if ($ChartOnly) { $null } else { '1' }

try
{
    dotnet test --project tests/DotNetDistributedApp.DeploymentTests
    exit $LASTEXITCODE
}
finally
{
    Remove-Item Env:\RUN_CHART_TESTS -ErrorAction SilentlyContinue
    Remove-Item Env:\RUN_DEPLOYMENT_TESTS -ErrorAction SilentlyContinue
}
