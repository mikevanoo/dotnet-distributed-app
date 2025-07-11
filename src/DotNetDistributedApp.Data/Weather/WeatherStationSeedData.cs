namespace DotNetDistributedApp.Api.Data.Weather;

public static class WeatherStationSeedData
{
    public static List<WeatherStation> Get() =>
    [
        Stornoway,
        Heathrow
    ];

    public static readonly WeatherStation Stornoway = new()
    {
        Id = 1,
        Key = "stornoway",
        DisplayName = "Stornoway",
        Latitude = 58.214m,
        Longitude = -6.318m,
    };

    public static readonly WeatherStation Heathrow = new()
    {
        Id = 2,
        Key = "heathrow",
        DisplayName = "Heathrow (London Airport)",
        Latitude = 51.479m,
        Longitude = -0.449m,
    };
}