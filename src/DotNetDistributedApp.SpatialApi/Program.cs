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
        .AddOpenApi()
        .AddValidation();

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
        ([AsParameters] ToOsNationalGridReferenceRequest request) =>
            CoordinateConverterService.ToOsgb36(request.Latitude, request.Longitude).ToApiResponse()
    );
    conversionGroup.MapGet(
        "/to-latitude-longitude",
        ([AsParameters] ToLatitudeLongitudeRequest request) =>
            CoordinateConverterService.ToWgs84(request.Easting, request.Northing).ToApiResponse()
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
