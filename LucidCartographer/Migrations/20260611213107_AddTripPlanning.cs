using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class AddTripPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FinishPoiId",
                table: "PoiCollections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartPoiId",
                table: "PoiCollections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeBudgetMinutes",
                table: "PoiCollections",
                type: "INTEGER",
                nullable: true);

            // TRIP-SCHEMA-03: default the new column to 'AnyAir' (the no-routing default), NOT ''.
            // This both backfills existing rows with a value that satisfies CK_PoiCollection_TravelMode
            // (added below — the SQLite table rebuild re-validates every copied row) and leaves a
            // valid surviving column DEFAULT, so a later raw INSERT omitting TravelMode cannot fail
            // the check constraint. EF-path inserts always supply the model default anyway.
            migrationBuilder.AddColumn<string>(
                name: "TravelMode",
                table: "PoiCollections",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "AnyAir");

            migrationBuilder.AddColumn<DateTime>(
                name: "TripStartTime",
                table: "PoiCollections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TripViewEnabled",
                table: "PoiCollections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DwellMinutes",
                table: "PoiCollectionItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderIndex",
                table: "PoiCollectionItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // TRIP-SCHEMA-02 backfill: a non-nullable int defaults to 0, which violates the
            // 1-based (1..N, contiguous, gap-free) Stop Order invariant for existing rows.
            // Assign a deterministic 1-based order per Collection over ONLY the placeable
            // members (both Latitude and Longitude present — the Story 1.2 runtime invariant
            // AR-11), ordered by POI AddedDate ascending (the seed rule), tie-broken by PoiId.
            // Non-placeable members (and LEFT JOIN orphans whose POI is absent) get OrderIndex 0
            // ("not a stop"), exactly as SeedOrderAsync would compute — so they never receive a
            // badge and the placeable numbering stays contiguous. The placeable-flag is the
            // leading ORDER BY key, so placeable rows take ROW_NUMBER 1..K and the CASE clamps
            // the rest to 0. SQLite ROW_NUMBER + UPDATE...FROM (requires SQLite >= 3.33, satisfied
            // by the bundled Microsoft.Data.Sqlite). Seeding/compaction at runtime is Story 1.2.
            migrationBuilder.Sql(@"
UPDATE PoiCollectionItems
SET OrderIndex = sub.rn
FROM (
    SELECT pci.PoiId, pci.PoiCollectionId,
           CASE
               WHEN p.Latitude IS NOT NULL AND p.Longitude IS NOT NULL
               THEN ROW_NUMBER() OVER (
                        PARTITION BY pci.PoiCollectionId
                        ORDER BY (CASE WHEN p.Latitude IS NOT NULL AND p.Longitude IS NOT NULL THEN 0 ELSE 1 END),
                                 (p.AddedDate IS NULL), p.AddedDate ASC, pci.PoiId ASC)
               ELSE 0
           END AS rn
    FROM PoiCollectionItems pci
    LEFT JOIN Pois p ON p.Id = pci.PoiId
) AS sub
WHERE PoiCollectionItems.PoiId = sub.PoiId
  AND PoiCollectionItems.PoiCollectionId = sub.PoiCollectionId;");

            migrationBuilder.CreateTable(
                name: "RouteSegments",
                columns: table => new
                {
                    FromPoiId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToPoiId = table.Column<int>(type: "INTEGER", nullable: false),
                    TravelMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    DistanceMeters = table.Column<double>(type: "REAL", nullable: false),
                    GeometryPolyline = table.Column<string>(type: "TEXT", nullable: true),
                    Fidelity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteSegments", x => new { x.FromPoiId, x.ToPoiId, x.TravelMode });
                    table.CheckConstraint("CK_RouteSegment_Fidelity", "Fidelity IN ('Measured','Estimated','Placeholder','Manual')");
                    table.CheckConstraint("CK_RouteSegment_TravelMode", "TravelMode IN ('AnyAir','Drive','Walk','Cycle')");
                    table.ForeignKey(
                        name: "FK_RouteSegments_Pois_FromPoiId",
                        column: x => x.FromPoiId,
                        principalTable: "Pois",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RouteSegments_Pois_ToPoiId",
                        column: x => x.ToPoiId,
                        principalTable: "Pois",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoiCollections_FinishPoiId",
                table: "PoiCollections",
                column: "FinishPoiId");

            migrationBuilder.CreateIndex(
                name: "IX_PoiCollections_StartPoiId",
                table: "PoiCollections",
                column: "StartPoiId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PoiCollection_TravelMode",
                table: "PoiCollections",
                sql: "TravelMode IN ('AnyAir','Drive','Walk','Cycle')");

            migrationBuilder.CreateIndex(
                name: "IX_RouteSegments_FromPoiId",
                table: "RouteSegments",
                column: "FromPoiId");

            migrationBuilder.CreateIndex(
                name: "IX_RouteSegments_ToPoiId",
                table: "RouteSegments",
                column: "ToPoiId");

            migrationBuilder.AddForeignKey(
                name: "FK_PoiCollections_Pois_FinishPoiId",
                table: "PoiCollections",
                column: "FinishPoiId",
                principalTable: "Pois",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PoiCollections_Pois_StartPoiId",
                table: "PoiCollections",
                column: "StartPoiId",
                principalTable: "Pois",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PoiCollections_Pois_FinishPoiId",
                table: "PoiCollections");

            migrationBuilder.DropForeignKey(
                name: "FK_PoiCollections_Pois_StartPoiId",
                table: "PoiCollections");

            migrationBuilder.DropTable(
                name: "RouteSegments");

            migrationBuilder.DropIndex(
                name: "IX_PoiCollections_FinishPoiId",
                table: "PoiCollections");

            migrationBuilder.DropIndex(
                name: "IX_PoiCollections_StartPoiId",
                table: "PoiCollections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PoiCollection_TravelMode",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "FinishPoiId",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "StartPoiId",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "TimeBudgetMinutes",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "TravelMode",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "TripStartTime",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "TripViewEnabled",
                table: "PoiCollections");

            migrationBuilder.DropColumn(
                name: "DwellMinutes",
                table: "PoiCollectionItems");

            migrationBuilder.DropColumn(
                name: "OrderIndex",
                table: "PoiCollectionItems");
        }
    }
}
