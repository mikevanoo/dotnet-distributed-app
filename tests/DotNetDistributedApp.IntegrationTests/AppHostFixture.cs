using Aspire.Hosting;
using DotNetDistributedApp.IntegrationTests;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(AppHostFixture))]

namespace DotNetDistributedApp.IntegrationTests;

public class AppHostFixture : IAsyncLifetime
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public DistributedApplication App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = CreateCancellationToken();
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DotNetDistributedApp_AppHost>(
            cancellationToken
        );
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            // Override the logging filters from the app's configuration
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
            // To output logs to the xUnit.net ITestOutputHelper, consider adding a package from https://www.nuget.org/packages?q=xunit+logging
        });
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        App = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await App.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        await App
            .ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken)
            .WaitAsync(AppHostFixture.DefaultTimeout, cancellationToken);
    }

    public static CancellationToken CreateCancellationToken(TimeSpan? timeout = null)
    {
        return new CancellationTokenSource(timeout ?? DefaultTimeout).Token;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
