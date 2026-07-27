using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotNetDistributedApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedWeatherEventsInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_weather_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_name = table.Column<string>(type: "text", nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    partition_key = table.Column<string>(type: "text", nullable: false),
                    consumer_group = table.Column<string>(type: "text", nullable: false),
                    partition = table.Column<int>(type: "integer", nullable: false),
                    offset = table.Column<long>(type: "bigint", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_weather_events", x => x.id);
                }
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
            migrationBuilder.DropTable(name: "processed_weather_events");
        }
    }
}
