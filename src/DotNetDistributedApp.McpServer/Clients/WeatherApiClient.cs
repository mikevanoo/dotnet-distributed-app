using System.Net;
using System.Text.Json;
using DotNetDistributedApp.Api.Common.Errors;
using FluentResults;

namespace DotNetDistributedApp.McpServer.Clients;

public partial class WeatherApiClient(HttpClient httpClient, ILogger<WeatherApiClient> logger)
{
    private const string NoDetail = "no further detail was provided";

    public async Task<Result<IReadOnlyList<WeatherStationDto>>> GetWeatherStations(
        CancellationToken cancellationToken
    ) =>
        (
            await Get<ResponseDtoOfListOfWeatherStationDto>(
                "/v2.0/weather/stations",
                "The list of weather stations",
                cancellationToken
            )
        ).Map<IReadOnlyList<WeatherStationDto>>(envelope => envelope?.Response?.ToArray() ?? []);

    public async Task<Result<IReadOnlyList<WeatherStationHistoricDataDto>>> GetWeatherStationHistoricData(
        string stationKey,
        int? fromYear,
        int? toYear,
        CancellationToken cancellationToken
    )
    {
        var requestUri =
            $"/v2.0/weather/stations/{Uri.EscapeDataString(stationKey)}/historic-data"
            + $"?fromYear={fromYear}&toYear={toYear}";

        return (
            await Get<ResponseDtoOfListOfWeatherStationHistoricDataDto>(
                requestUri,
                $"Historic data for weather station '{stationKey}'",
                cancellationToken
            )
        ).Map<IReadOnlyList<WeatherStationHistoricDataDto>>(envelope => envelope?.Response?.ToArray() ?? []);
    }

    /// <summary>
    /// Failures carry the reason back to the caller instead of surfacing as a bare status code: the weather API
    /// answers an invalid request with a ProblemDetails body naming the offending field, and that detail is the only
    /// thing that lets a calling model correct its own request.
    /// </summary>
    private async Task<Result<TResponse?>> Get<TResponse>(
        string requestUri,
        string resourceDescription,
        CancellationToken cancellationToken
    )
        where TResponse : class
    {
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        // The resilience fallback in ConfigureWeatherApiHttpClient answers with 204 when the weather API cannot be
        // reached at all, so a 204 here is an outage and must not be reported as an empty result.
        if (response.StatusCode is HttpStatusCode.NoContent)
        {
            LogWeatherApiUnreachable(requestUri);

            return Result.Fail(
                new Error(
                    "The weather API could not be reached, so no data could be retrieved. This is a temporary "
                        + "service failure rather than a missing station or a station with no readings; "
                        + "retrying may succeed."
                )
            );
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            LogWeatherApiRequestFailed(requestUri, (int)response.StatusCode, "not found");

            return Result.Fail(new NotFoundError($"{resourceDescription} was not found by the weather API."));
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadProblemDetail(response, cancellationToken);
            LogWeatherApiRequestFailed(requestUri, (int)response.StatusCode, detail);

            return Result.Fail(
                new Error(
                    $"The weather API rejected the request with {(int)response.StatusCode} "
                        + $"{response.StatusCode}: {detail}"
                )
            );
        }

        try
        {
            return Result.Ok(await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken));
        }
        catch (JsonException exception)
        {
            LogWeatherApiResponseUnreadable(exception, requestUri);

            return Result.Fail(
                new Error(
                    $"The weather API answered successfully but the response could not be read: {exception.Message}"
                )
            );
        }
    }

    /// <summary>
    /// Reads the useful text out of a ProblemDetails body. Validation failures put it in the per-field
    /// <c>errors</c> dictionary; every other failure puts it in <c>detail</c>, falling back to <c>title</c>.
    /// </summary>
    private static async Task<string> ReadProblemDetail(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (
                await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(cancellationToken)
                is not { } problem
            )
            {
                return NoDetail;
            }

            var fieldErrors = problem
                .Errors.SelectMany(field => field.Value.Select(message => $"{field.Key}: {message}"))
                .ToArray();

            return fieldErrors.Length is not 0
                ? string.Join(" ", fieldErrors)
                : problem.Detail ?? problem.Title ?? NoDetail;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            // An error body that is not ProblemDetails at all - an HTML gateway page, say - leaves only the status.
            return NoDetail;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Weather API request to {RequestUri} failed with status {StatusCode}: {Detail}")]
    private partial void LogWeatherApiRequestFailed(string requestUri, int statusCode, string detail);

    [LoggerMessage(LogLevel.Error, "Weather API could not be reached for the request to {RequestUri}")]
    private partial void LogWeatherApiUnreachable(string requestUri);

    [LoggerMessage(LogLevel.Error, "Weather API response to {RequestUri} could not be deserialized")]
    private partial void LogWeatherApiResponseUnreadable(Exception exception, string requestUri);
}
