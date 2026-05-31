using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class AddPoiEnrichmentRequested : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pois_IsEnriched_EnrichmentFailureCount_LastEnrichmentAttemptAt",
                table: "Pois");

            migrationBuilder.AddColumn<bool>(
                name: "EnrichmentRequested",
                table: "Pois",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // DATA BACKFILL — preserve the existing enrichment queue across the
            // switch from the implicit "IsEnriched==false means enqueue" model to
            // the explicit EnrichmentRequested flag. Flag every not-yet-enriched
            // row; the worker's own "EnrichmentFailureCount < MaxRetries" filter
            // still gates processing, and PersistFailureAsync clears the flag once
            // a row reaches the cap — so over-flagging is self-healing and stays
            // correct regardless of the configured MaxRetries (we deliberately do
            // NOT hardcode the cap here, which would mis-flag rows on a non-default
            // MaxRetries deployment).
            migrationBuilder.Sql(
                "UPDATE Pois SET EnrichmentRequested = 1 WHERE IsEnriched = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_Pois_EnrichmentRequested_EnrichmentFailureCount_LastEnrichmentAttemptAt",
                table: "Pois",
                columns: new[] { "EnrichmentRequested", "EnrichmentFailureCount", "LastEnrichmentAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pois_EnrichmentRequested_EnrichmentFailureCount_LastEnrichmentAttemptAt",
                table: "Pois");

            migrationBuilder.DropColumn(
                name: "EnrichmentRequested",
                table: "Pois");

            migrationBuilder.CreateIndex(
                name: "IX_Pois_IsEnriched_EnrichmentFailureCount_LastEnrichmentAttemptAt",
                table: "Pois",
                columns: new[] { "IsEnriched", "EnrichmentFailureCount", "LastEnrichmentAttemptAt" });
        }
    }
}
