using System.Globalization;
using System.Text.Json.Serialization;
using Asp.Versioning;
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
        .AddValidation()
        // Ordering for versioning and OpenApi is essential: AddApiVersion() then AddApiExplorer() then AddOpenApi()
        .AddApiVersioning(options =>
        {
            // API versioning by URL segment (api/v1/users)
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
        })
        .AddOpenApi();
    ;

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().WithDocumentPerVersion();
        app.MapScalarApiReference(options =>
        {
            var descriptions = app.DescribeApiVersions();
            for (var index = 0; index < descriptions.Count; index++)
            {
                var description = descriptions[index];
                var isDefault = index == descriptions.Count - 1;
                options.AddDocument(description.GroupName, description.GroupName, isDefault: isDefault);
            }
        });
    }

    var api = app.NewVersionedApi("Spatial");
    var v1 = api.MapGroup("/v{version:apiVersion}/coordinate-converter").HasApiVersion(1.0);
    v1.MapGet(
        "/to-os-national-grid-reference",
        ([AsParameters] ToOsNationalGridReferenceRequest request) =>
            CoordinateConverterService.ToOsgb36(request.Latitude, request.Longitude).ToApiResponse()
    );
    v1.MapGet(
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
