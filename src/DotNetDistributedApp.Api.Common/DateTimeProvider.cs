namespace DotNetDistributedApp.Api.Common;

public interface IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; }
}

public class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
}
