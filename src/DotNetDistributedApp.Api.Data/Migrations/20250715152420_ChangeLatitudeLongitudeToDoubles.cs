using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotNetDistributedApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeLatitudeLongitudeToDoubles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "longitude",
                table: "weather_stations",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<double>(
                name: "latitude",
                table: "weather_stations",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.UpdateData(
                table: "weather_stations",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "latitude", "longitude" },
                values: new object[] { 58.214000701904297, -6.3179998397827148 });

            migrationBuilder.UpdateData(
                table: "weather_stations",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "latitude", "longitude" },
                values: new object[] { 51.479000091552734, -0.44900000095367432 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "longitude",
                table: "weather_stations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AlterColumn<decimal>(
                name: "latitude",
                table: "weather_stations",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.UpdateData(
                table: "weather_stations",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "latitude", "longitude" },
                values: new object[] { 58.214m, -6.318m });

            migrationBuilder.UpdateData(
                table: "weather_stations",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "latitude", "longitude" },
                values: new object[] { 51.479m, -0.449m });
        }
    }
}
