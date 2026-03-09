using System.Globalization;
using DotNetDistributedApp.Api.Common.Errors;
using FluentResults;

namespace DotNetDistributedApp.Api.Clients;

public class CoordinateConverterClient(HttpClient httpClient)
{
    public async Task<Result<OsNationalGridReferenceDto>> ToOsNationalGridReference(double latitude, double longitude)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "/coordinate-converter/to-os-national-grid-reference?latitude={0}&longitude={1}",
            latitude,
            longitude
        );
        var result = await httpClient.GetFromJsonAsync<OsNationalGridReferenceDto>(url);
        if (result is null)
        {
            return Result.Fail(
                new NotFoundError(
                    $"Could not convert latitude={latitude} longitude={longitude} to OS National Grid Reference"
                )
            );
        }

        return Result.Ok(result);
    }
}
