using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public static class LoggerTestExtensions
{
    public static void ShouldHaveLogged<T>(
        this FakeLogger<T> logger,
        LogLevel expectedLogLevel,
        string expectedMessage
    ) =>
        logger
            .Collector.GetSnapshot()
            .Should()
            .Contain(x => x.Level == expectedLogLevel && x.Message == expectedMessage);
}
