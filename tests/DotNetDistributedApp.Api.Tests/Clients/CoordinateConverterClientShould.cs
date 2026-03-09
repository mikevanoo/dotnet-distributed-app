using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Clients;

namespace DotNetDistributedApp.Api.Tests.Clients;

public class CoordinateConverterClientShould
{
    [Fact]
    public async Task FormatCoordinatesWithInvariantCultureInUrl()
    {
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            Uri? capturedUri = null;
            var response = new OsNationalGridReferenceDto { Easting = 507800, Northing = 176700 };
            var handler = new CapturingHttpMessageHandler(request =>
            {
                capturedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(response),
                        System.Text.Encoding.UTF8,
                        "application/json"
                    ),
                };
            });

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
            var client = new CoordinateConverterClient(httpClient);

            await client.ToOsNationalGridReference(51.479, -0.449, TestContext.Current.CancellationToken);

            capturedUri.Should().NotBeNull();
            capturedUri.Query.Should().Contain("latitude=51.479");
            capturedUri.Query.Should().Contain("longitude=-0.449");
            capturedUri.Query.Should().NotContain(",");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    private class CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(handler(request));
    }
}
