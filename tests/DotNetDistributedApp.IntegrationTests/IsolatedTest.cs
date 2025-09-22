using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.IntegrationTests;

/*
 * Use this to run a test in isolation with extra logging via ITestOutputHelper.
 * Comment out all other tests and AppHostFixture.cs and run via `dotnet test`.
 */

// public class IsolatedTest(ITestOutputHelper testOutputHelper)
// {
//     private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
//
//     [Fact]
//     public async Task GetWebResourceRootReturnsOkStatusCode()
//     {
//         // Arrange
//         var cancellationToken = new CancellationTokenSource(DefaultTimeout).Token;
//         var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DotNetDistributedApp_AppHost>(
//             cancellationToken
//         );
//         appHost.Services.AddLogging(logging =>
//         {
//             logging.SetMinimumLevel(LogLevel.Debug);
//             logging.AddXUnit(testOutputHelper);
//             // Override the logging filters from the app's configuration
//             logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
//             logging.AddFilter("Aspire.", LogLevel.Debug);
//         });
//         appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
//         {
//             clientBuilder.AddStandardResilienceHandler();
//         });
//
//         await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
//         await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
//         await app
//             .ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken)
//             .WaitAsync(DefaultTimeout, cancellationToken);
//
//         // Act
//         var httpClient = app.CreateHttpClient("spatial-api");
//         await app
//             .ResourceNotifications.WaitForResourceHealthyAsync("spatial-api", cancellationToken)
//             .WaitAsync(DefaultTimeout, cancellationToken);
//
//         var response = await httpClient.GetAsync(
//             "/coordinate-converter/to-os-national-grid-reference?latitude=58.214&longitude=-6.318",
//             cancellationToken
//         );
//
//         // Assert
//         response.Should().Be200Ok();
//     }
// }
