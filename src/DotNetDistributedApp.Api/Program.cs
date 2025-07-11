using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services
    .AddApiDatabaseContext<WeatherDbContext>(builder.Configuration)
    .AddScoped<WeatherService>();

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

app.MapGet("/weather/stations", async ([FromServices] WeatherService weatherService) => 
    await weatherService.GetWeatherStations());

app.MapGet("/weather/stations/{stationKey}/historic-data", 
    async Task<Results<Ok<List<WeatherStationHistoricDataDto>>, NotFound>> (
        [FromServices] WeatherService weatherService, string stationKey) =>
{
    var data = await weatherService.GetWeatherStationHistoricData(stationKey);
    if (data is null)
    {
        return TypedResults.NotFound();
    }
    
    return TypedResults.Ok(data);
});

app.MapDefaultEndpoints();

app.Run();
