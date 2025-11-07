using System.Globalization;
using DotNetDistributedApp.Api;
using DotNetDistributedApp.Api.Events;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddCoreServices().AddCachingServices().AddEventServices();

    var app = builder.Build();
    app.UseCoreMiddleware().MapWeatherEndpoints().MapEventsEndpoints();
    app.MapDefaultEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
