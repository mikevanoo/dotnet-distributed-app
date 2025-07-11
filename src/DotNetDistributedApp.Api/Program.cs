using System.Text.Json.Serialization;
using DotNetDistributedApp.Api;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder
    .Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    })
    .AddProblemDetails()
    .AddOpenApi();

builder.Services.AddApiDatabaseContext<WeatherDbContext>(builder.Configuration).AddScoped<WeatherService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "v1");
    });
}

var weatherGroup = app.MapGroup("/weather");
weatherGroup.MapGet(
    "/stations",
    async ([FromServices] WeatherService weatherService) => (await weatherService.GetWeatherStations()).ToApiResponse()
);
weatherGroup.MapGet(
    "/stations/{stationKey}/historic-data",
    async ([FromServices] WeatherService weatherService, string stationKey) =>
        (await weatherService.GetWeatherStationHistoricData(stationKey)).ToApiResponse()
);

app.MapDefaultEndpoints();

app.Run();
