using DotNetDistributedApp.Api.Common.Errors;
using FluentResults;

namespace DotNetDistributedApp.Api.Clients;

public class CoordinateConverterClient(HttpClient httpClient)
{
    public async Task<Result<OsNationalGridReferenceDto>> ToOsNationalGridReference(double latitude, double longitude)
    {
        var url = $"/coordinate-converter/to-os-national-grid-reference?latitude={latitude}&longitude={longitude}";
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
