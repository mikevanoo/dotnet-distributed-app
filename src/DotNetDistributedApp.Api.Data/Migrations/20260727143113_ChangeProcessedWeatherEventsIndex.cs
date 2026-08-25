using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotNetDistributedApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeProcessedWeatherEventsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_processed_weather_events_id_processed_at_utc",
                table: "processed_weather_events"
            );

            migrationBuilder.CreateIndex(
                name: "ix_processed_weather_events_event_name_processed_at_utc",
                table: "processed_weather_events",
                columns: new[] { "event_name", "processed_at_utc" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_processed_weather_events_event_name_processed_at_utc",
                table: "processed_weather_events"
            );

            migrationBuilder.CreateIndex(
                name: "ix_processed_weather_events_id_processed_at_utc",
                table: "processed_weather_events",
                columns: new[] { "id", "processed_at_utc" }
            );
        }
    }
}
