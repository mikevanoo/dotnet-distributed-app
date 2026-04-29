using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Clients;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DotNetDistributedApp.Api.Tests.Clients;

public class CoordinateConverterClientShould
{
    private readonly ILogger<CoordinateConverterClient> _logger = Substitute.For<ILogger<CoordinateConverterClient>>();

    [Fact]
    public async Task FormatCoordinatesWithInvariantCultureInUrl()
    {
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            Uri? capturedUri = null;
            var client = CreateClient(request =>
            {
                capturedUri = request.RequestUri;
                return JsonResponse(new OsNationalGridReferenceDto { Easting = 507800, Northing = 176700 });
            });

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

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReturnFailResultWhenSpatialApiReturnsErrorStatusCode(HttpStatusCode statusCode)
    {
        var client = CreateClient(_ => new HttpResponseMessage(statusCode));

        var result = await client.ToOsNationalGridReference(51.479, -0.449, TestContext.Current.CancellationToken);

        result.IsFailed.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("Coordinate conversion failed");
    }

    [Fact]
    public async Task ReturnOkResultWhenSpatialApiReturnsSuccess()
    {
        var client = CreateClient(_ =>
            JsonResponse(new OsNationalGridReferenceDto { Easting = 507800, Northing = 176700 })
        );

        var result = await client.ToOsNationalGridReference(51.479, -0.449, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Easting.Should().Be(507800);
        result.Value.Northing.Should().Be(176700);
    }

    [Fact]
    public async Task ReturnSuccessfulNullValueWhenHandlerReturnsNoContent()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await client.ToOsNationalGridReference(51.479, -0.449, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    private CoordinateConverterClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return new CoordinateConverterClient(httpClient, _logger);
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
