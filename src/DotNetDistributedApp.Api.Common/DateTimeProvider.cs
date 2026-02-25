namespace DotNetDistributedApp.Api.Common;

// TODO remove?
public interface IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; }
}

public class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
}
