using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentFailureTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnrichmentFailureCount",
                table: "Pois",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEnrichmentAttemptAt",
                table: "Pois",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pois_IsEnriched_EnrichmentFailureCount_LastEnrichmentAttemptAt",
                table: "Pois",
                columns: new[] { "IsEnriched", "EnrichmentFailureCount", "LastEnrichmentAttemptAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Poi_EnrichmentFailureCount",
                table: "Pois",
                sql: "EnrichmentFailureCount >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pois_IsEnriched_EnrichmentFailureCount_LastEnrichmentAttemptAt",
                table: "Pois");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Poi_EnrichmentFailureCount",
                table: "Pois");

            migrationBuilder.DropColumn(
                name: "EnrichmentFailureCount",
                table: "Pois");

            migrationBuilder.DropColumn(
                name: "LastEnrichmentAttemptAt",
                table: "Pois");
        }
    }
}
