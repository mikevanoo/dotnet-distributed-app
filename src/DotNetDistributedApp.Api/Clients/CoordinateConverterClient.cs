using System.Globalization;
using System.Net;
using FluentResults;

namespace DotNetDistributedApp.Api.Clients;

public partial class CoordinateConverterClient(HttpClient httpClient, ILogger<CoordinateConverterClient> logger)
{
    public async Task<Result<OsNationalGridReferenceDto?>> ToOsNationalGridReference(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    )
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "/v1.0/coordinate-converter/to-os-national-grid-reference?latitude={0}&longitude={1}",
            latitude,
            longitude
        );

        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                LogResilienceFallbackUsed(latitude, longitude);
                return Result.Ok<OsNationalGridReferenceDto?>(null);
            }

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OsNationalGridReferenceDto>(cancellationToken);
            if (result is null)
            {
                return Result.Fail(
                    $"Coordinate conversion returned null for latitude={latitude}, longitude={longitude}"
                );
            }

            return Result.Ok<OsNationalGridReferenceDto?>(result);
        }
        catch (HttpRequestException ex)
        {
            LogFailedToConvertCoordinates(ex, latitude, longitude, ex.StatusCode);
            return Result.Fail(
                $"Coordinate conversion failed for latitude={latitude}, longitude={longitude}: {ex.Message}"
            );
        }
    }

    [LoggerMessage(
        LogLevel.Warning,
        "Failed to convert coordinates (latitude={Latitude}, longitude={Longitude}): {StatusCode}"
    )]
    private partial void LogFailedToConvertCoordinates(
        Exception ex,
        double latitude,
        double longitude,
        HttpStatusCode? statusCode
    );

    [LoggerMessage(
        LogLevel.Warning,
        "Coordinate conversion fell back for latitude={Latitude}, longitude={Longitude}; "
            + "SpatialApi was unreachable or pipeline exhausted. Returning null grid reference."
    )]
    private partial void LogResilienceFallbackUsed(double latitude, double longitude);
}
