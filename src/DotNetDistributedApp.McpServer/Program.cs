using System.Globalization;
using DotNetDistributedApp.McpServer;
using DotNetDistributedApp.ServiceDefaults;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddCoreServices().AddMcpServerServices();

    var app = builder.Build();
    app.MapDefaultEndpoints().MapMcp("/mcp");

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
