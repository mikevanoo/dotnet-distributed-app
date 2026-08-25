namespace DotNetDistributedApp.ScheduledTasks.ProcessedWeatherEvents;

public class ProcessedWeatherEventsCleanerOptions
{
    public string Cron { get; set; } = "0 * * * *"; // defaults to hourly
    public TimeSpan DefaultRetention { get; set; } = TimeSpan.FromDays(1);
    public Dictionary<string, TimeSpan> RetentionByEventName { get; set; } = new();
}
