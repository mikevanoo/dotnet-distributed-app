using System.Text.Json;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Clients;
using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DotNetDistributedApp.Api.Tests.Clients;

public class GeoIpClientShould
{
    private readonly ILogger<GeoIpClient> _logger = Substitute.For<ILogger<GeoIpClient>>();
    private readonly IMetricsService _metricsService = Substitute.For<IMetricsService>();

    [Fact]
    public async Task ReturnDeserializedResponseWhenGeoIpApiReturnsSuccess()
    {
        var client = CreateClient(_ =>
            JsonResponse(
                new GeoIpResponseDto
                {
                    Country = "US",
                    Latitude = 37.751,
                    Longitude = -97.822,
                    Continent = "NA",
                    Timezone = "America/Chicago",
                    AsnOrganization = "Google LLC",
                    AsnNetwork = "8.8.8.0/24",
                }
            )
        );

        var result = await client.GetGeoInformation("8.8.8.8", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Country.Should().Be("US");
        result.Latitude.Should().Be(37.751);
    }

    [Fact]
    public async Task ReturnNullWhenHandlerReturnsNoContent()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await client.GetGeoInformation("8.8.8.8", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReturnNullWhenGeoIpApiReturnsErrorStatusCode(HttpStatusCode statusCode)
    {
        var client = CreateClient(_ => new HttpResponseMessage(statusCode));

        var result = await client.GetGeoInformation("8.8.8.8", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    private GeoIpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return new GeoIpClient(httpClient, CreateHybridCache(), _logger, _metricsService);
    }

    private static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };

    private class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(handler(request));
    }
}
