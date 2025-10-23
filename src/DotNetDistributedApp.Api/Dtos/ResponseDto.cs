using DotNetDistributedApp.Api.Clients;

namespace DotNetDistributedApp.Api.DTOs;

public class ResponseDto
{
    public ResponseMetadataDto Metadata { get; private set; } = new();

    public static ResponseDto<T> Create<T>(T response, GeoIpResponseDto? geoData = null)
    {
        var result = new ResponseDto<T>(response);
        if (geoData is not null)
        {
            result.Metadata.GeoData = geoData;
        }

        return result;
    }
}

public class ResponseDto<T>(T response) : ResponseDto
{
    public T Response { get; set; } = response;
}

public class ResponseMetadataDto
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
    public GeoIpResponseDto? GeoData { get; set; }
}
