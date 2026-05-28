using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class RemovePoiStatusAndVisitedDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pois_Status",
                table: "Pois");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Pois");

            migrationBuilder.DropColumn(
                name: "VisitedDate",
                table: "Pois");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Pois",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VisitedDate",
                table: "Pois",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pois_Status",
                table: "Pois",
                column: "Status");
        }
    }
}
