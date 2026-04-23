using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class PoiCoordsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Poi_Latitude",
                table: "Pois");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Poi_Longitude",
                table: "Pois");

            migrationBuilder.AlterColumn<double>(
                name: "Longitude",
                table: "Pois",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<double>(
                name: "Latitude",
                table: "Pois",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Poi_Latitude",
                table: "Pois",
                sql: "Latitude IS NULL OR (Latitude >= -90 AND Latitude <= 90)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Poi_Longitude",
                table: "Pois",
                sql: "Longitude IS NULL OR (Longitude >= -180 AND Longitude <= 180)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Poi_Latitude",
                table: "Pois");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Poi_Longitude",
                table: "Pois");

            migrationBuilder.AlterColumn<double>(
                name: "Longitude",
                table: "Pois",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "Latitude",
                table: "Pois",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Poi_Latitude",
                table: "Pois",
                sql: "Latitude >= -90 AND Latitude <= 90");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Poi_Longitude",
                table: "Pois",
                sql: "Longitude >= -180 AND Longitude <= 180");
        }
    }
}
