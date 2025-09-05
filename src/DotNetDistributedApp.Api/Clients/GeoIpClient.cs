using DotNetDistributedApp.Api.Common.Errors;
using FluentResults;

namespace DotNetDistributedApp.Api.Clients;

public class GeoIpClient(HttpClient httpClient)
{
    public async Task<Result<GeoIpResponseDto>> GetGeoInformation(string ipAddress)
    {
        var url = $"/{ipAddress}";
        var result = await httpClient.GetFromJsonAsync<GeoIpResponseDto>(url);
        if (result is null)
        {
            return Result.Fail(new NotFoundError($"Could not get geo information for IP address {ipAddress}"));
        }

        return Result.Ok(result);
    }
}
