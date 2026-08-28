using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace DotNetDistributedApp.Api.Weather;

public class GetWeatherStationHistoricDataRequest : IValidatableObject
{
    // The earliest UK Met Office observations pre-date the seeded data (1873), so the floor is deliberately generous.
    private const int EarliestSupportedYear = 1800;
    private const int LatestSupportedYear = 2100;

    [Required]
    public string StationKey { get; init; } = string.Empty;

    [FromQuery(Name = "fromYear")]
    [Range(EarliestSupportedYear, LatestSupportedYear)]
    public int? FromYear { get; init; }

    [FromQuery(Name = "toYear")]
    [Range(EarliestSupportedYear, LatestSupportedYear)]
    public int? ToYear { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FromYear.HasValue && ToYear.HasValue && FromYear > ToYear)
        {
            yield return new ValidationResult(
                $"{nameof(FromYear)} must not be later than {nameof(ToYear)}.",
                [nameof(FromYear), nameof(ToYear)]
            );
        }
    }
}
