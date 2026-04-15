using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class AddPoiIsEnriched : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnriched",
                table: "Pois",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Pois_IsEnriched",
                table: "Pois",
                column: "IsEnriched");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pois_IsEnriched",
                table: "Pois");

            migrationBuilder.DropColumn(
                name: "IsEnriched",
                table: "Pois");
        }
    }
}
