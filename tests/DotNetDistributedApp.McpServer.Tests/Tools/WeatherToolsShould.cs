using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DotNetDistributedApp.McpServer.Clients;
using DotNetDistributedApp.McpServer.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using NSubstitute;

namespace DotNetDistributedApp.McpServer.Tests.Tools;

public class WeatherToolsShould
{
    private readonly ILogger<WeatherApiClient> _logger = Substitute.For<ILogger<WeatherApiClient>>();

    [Fact]
    public async Task NameBothYearsWhenTheRangeIsInvertedWithoutCallingTheWeatherApi()
    {
        var requests = 0;
        var tools = CreateTools(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData(
                "heathrow",
                2024,
                2023,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*fromYear (2024)*toYear (2023)*");
        requests.Should().Be(0);
    }

    [Fact]
    public async Task SurfaceThePerFieldValidationDetailFromTheWeatherApi()
    {
        var tools = CreateTools(_ =>
            JsonResponse(
                HttpStatusCode.BadRequest,
                new
                {
                    title = "One or more validation errors occurred.",
                    status = 400,
                    errors = new Dictionary<string, string[]>
                    {
                        ["FromYear"] = ["The field FromYear must be between 1800 and 2100."],
                    },
                }
            )
        );

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData(
                "heathrow",
                1700,
                2000,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<McpException>()).WithMessage(
            "*400*FromYear: The field FromYear must be between 1800 and 2100.*"
        );
    }

    [Fact]
    public async Task SurfaceTheProblemDetailsDetailWhenThereAreNoPerFieldErrors()
    {
        var tools = CreateTools(_ =>
            JsonResponse(
                HttpStatusCode.BadRequest,
                new
                {
                    title = "The request could not be processed by the server",
                    detail = "The weather station key was not supplied",
                    status = 400,
                }
            )
        );

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData("", 2000, 2010, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*The weather station key was not supplied*");
    }

    [Fact]
    public async Task PointAtListWeatherStationsWhenTheStationKeyIsNotFound()
    {
        var tools = CreateTools(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData(
                "nosuchstation",
                2000,
                2010,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*nosuchstation*list_weather_stations*");
    }

    [Fact]
    public async Task NotOfferTheStationKeyHintForAFailureThatIsNotAMissingStation()
    {
        var tools = CreateTools(_ =>
            JsonResponse(HttpStatusCode.BadRequest, new { detail = "Something else went wrong", status = 400 })
        );

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData(
                "heathrow",
                2000,
                2010,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().NotContain("list_weather_stations");
    }

    [Fact]
    public async Task SayTheWeatherApiWasUnreachableRatherThanReportNoReadings()
    {
        // The resilience fallback answers with 204 when the weather API cannot be reached at all, which would
        // otherwise deserialize to an empty list and read as "this station has no readings".
        var tools = CreateTools(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData(
                "heathrow",
                2000,
                2010,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*could not be reached*");
    }

    [Fact]
    public async Task ReportTheStatusCodeWhenTheWeatherApiFailsWithoutAProblemDetailsBody()
    {
        var tools = CreateTools(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = async () =>
            await tools.SummariseWeatherStationHistoricData(
                "heathrow",
                2000,
                2010,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*500*no further detail*");
    }

    [Fact]
    public async Task SurfaceTheDetailFromAFailedListWeatherStationsCall()
    {
        var tools = CreateTools(_ =>
            JsonResponse(HttpStatusCode.BadGateway, new { detail = "Upstream is down", status = 502 })
        );

        var act = async () => await tools.ListWeatherStations(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*502*Upstream is down*");
    }

    [Fact]
    public async Task SummariseTheReadingsWhenTheWeatherApiSucceeds()
    {
        var tools = CreateTools(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                new
                {
                    response = new[]
                    {
                        new
                        {
                            year = 2021,
                            month = 1,
                            meanDailyMaxTemperature = 8.0,
                            totalRainfallMillimeters = 55.5,
                        },
                    },
                }
            )
        );

        var actual = await tools.SummariseWeatherStationHistoricData(
            "heathrow",
            2021,
            2021,
            TestContext.Current.CancellationToken
        );

        actual.FromYear.Should().Be(2021);
        actual.MeanDailyMaxTemperature.Should().Be(8.0);
        actual.TotalRainfallMillimeters.Should().Be(55.5);
        actual.Coverage.MonthsReturned.Should().Be(1);
    }

    /// <summary>
    /// A null year means "no bound", which the weather API expresses by the parameter being absent. Sending
    /// it as an empty value instead is a different request: the API binds "" to a nullable int as a
    /// validation failure and answers 400, so an unfiltered range failed outright.
    /// </summary>
    [Theory]
    [InlineData(null, null, "/v2.0/weather/stations/heathrow/historic-data")]
    [InlineData(1950, null, "/v2.0/weather/stations/heathrow/historic-data?fromYear=1950")]
    [InlineData(null, 1960, "/v2.0/weather/stations/heathrow/historic-data?toYear=1960")]
    [InlineData(1950, 1960, "/v2.0/weather/stations/heathrow/historic-data?fromYear=1950&toYear=1960")]
    public async Task OmitAYearFilterFromTheQueryStringWhenItIsNotSet(int? fromYear, int? toYear, string expectedUri)
    {
        var requestedUris = new List<string>();
        var tools = CreateTools(request =>
        {
            requestedUris.Add(request.RequestUri!.PathAndQuery);
            return JsonResponse(HttpStatusCode.OK, new { response = Array.Empty<object>() });
        });

        await tools.GetWeatherStationHistoricData("heathrow", fromYear, toYear, TestContext.Current.CancellationToken);

        requestedUris.Should().ContainSingle().Which.Should().Be(expectedUri);
    }

    private WeatherTools CreateTools(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        return new WeatherTools(new WeatherApiClient(httpClient, _logger));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(handler(request));
    }
}
