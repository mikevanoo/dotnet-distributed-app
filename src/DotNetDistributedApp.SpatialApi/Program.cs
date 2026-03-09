using System.Globalization;
using System.Text.Json.Serialization;
using DotNetDistributedApp.Api.Common;
using DotNetDistributedApp.ServiceDefaults;
using DotNetDistributedApp.SpatialApi.CoordinateConverter;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add service defaults & Aspire client integrations.
    builder.AddServiceDefaults();

    // Add services to the container.
    builder
        .Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration))
        .ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        })
        .AddProblemDetails()
        .AddOpenApi();

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    var conversionGroup = app.MapGroup("/coordinate-converter");
    conversionGroup.MapGet(
        "/to-os-national-grid-reference",
        (double latitude, double longitude) => CoordinateConverterService.ToOsgb36(latitude, longitude).ToApiResponse()
    );
    conversionGroup.MapGet(
        "/to-latitude-longitude",
        (double easting, double northing) => CoordinateConverterService.ToWgs84(easting, northing).ToApiResponse()
    );

    app.MapDefaultEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
