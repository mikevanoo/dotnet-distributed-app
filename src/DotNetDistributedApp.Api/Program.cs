using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddDbContextPool<WeatherDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("api-database")
                           ?? throw new InvalidOperationException("Connection string 'api-database' not found.");
    options
        .UseNpgsql(
            connectionString,
            x => x
                .MigrationsHistoryTable("__efmigrationshistory", "public")
                .CommandTimeout((int)TimeSpan.FromMinutes(5).TotalSeconds)
            )
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention();
});
builder.Services.AddScoped<WeatherService>();

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

app.MapDefaultEndpoints();

app.Run();
