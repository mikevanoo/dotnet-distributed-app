using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DotNetDistributedApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWeatherStationSeedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "name",
                table: "weather_stations",
                newName: "key");

            migrationBuilder.RenameIndex(
                name: "ix_weather_stations_name",
                table: "weather_stations",
                newName: "ix_weather_stations_key");

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "weather_stations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.InsertData(
                table: "weather_stations",
                columns: new[] { "id", "display_name", "key", "latitude", "longitude" },
                values: new object[,]
                {
                    { 1, "Stornoway", "stornoway", 58.214m, -6.318m },
                    { 2, "Heathrow (London Airport)", "heathrow", 51.479m, -0.449m }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "weather_stations",
                keyColumn: "id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "weather_stations",
                keyColumn: "id",
                keyValue: 2);

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "weather_stations");

            migrationBuilder.RenameColumn(
                name: "key",
                table: "weather_stations",
                newName: "name");

            migrationBuilder.RenameIndex(
                name: "ix_weather_stations_key",
                table: "weather_stations",
                newName: "ix_weather_stations_name");
        }
    }
}
