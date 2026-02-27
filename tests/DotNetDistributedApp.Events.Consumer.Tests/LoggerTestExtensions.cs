using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public static class LoggerTestExtensions
{
    public static void ShouldHaveLogged<T>(this FakeLogger<T> logger, LogLevel expectedLogLevel, string expectedMessage)
    {
        var snapshot = logger.Collector.GetSnapshot();
        var containsEntry = snapshot.Any(x => x.Level == expectedLogLevel && x.Message == expectedMessage);
        var actualLogs = string.Join(Environment.NewLine, snapshot.Select(x => $"[{x.Level}] {x.Message}"));

        containsEntry
            .Should()
            .BeTrue(
                $"""
                logger should contain this entry:
                [{expectedLogLevel}] {expectedMessage}

                but found:
                {actualLogs}{Environment.NewLine}
                """
            );
    }
}
