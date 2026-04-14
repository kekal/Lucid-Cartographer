using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PoiCollections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IconName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoiCollections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pois",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    GoogleMapsUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    GoogleRating = table.Column<double>(type: "REAL", nullable: true),
                    ReviewCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Website = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Country = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddedDate = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    VisitedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pois", x => x.Id);
                    table.CheckConstraint("CK_Poi_GoogleRating", "GoogleRating IS NULL OR (GoogleRating >= 1.0 AND GoogleRating <= 5.0)");
                    table.CheckConstraint("CK_Poi_Latitude", "Latitude >= -90 AND Latitude <= 90");
                    table.CheckConstraint("CK_Poi_Longitude", "Longitude >= -180 AND Longitude <= 180");
                    table.CheckConstraint("CK_Poi_Rating", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)");
                    table.CheckConstraint("CK_Poi_ReviewCount", "ReviewCount IS NULL OR ReviewCount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PoiCollectionItems",
                columns: table => new
                {
                    PoiId = table.Column<int>(type: "INTEGER", nullable: false),
                    PoiCollectionId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoiCollectionItems", x => new { x.PoiId, x.PoiCollectionId });
                    table.ForeignKey(
                        name: "FK_PoiCollectionItems_PoiCollections_PoiCollectionId",
                        column: x => x.PoiCollectionId,
                        principalTable: "PoiCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PoiCollectionItems_Pois_PoiId",
                        column: x => x.PoiId,
                        principalTable: "Pois",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PoiTags",
                columns: table => new
                {
                    PoiId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoiTags", x => new { x.PoiId, x.TagId });
                    table.ForeignKey(
                        name: "FK_PoiTags_Pois_PoiId",
                        column: x => x.PoiId,
                        principalTable: "Pois",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PoiTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoiCollectionItems_PoiCollectionId",
                table: "PoiCollectionItems",
                column: "PoiCollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_PoiCollections_Name",
                table: "PoiCollections",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Pois_GoogleMapsUrl",
                table: "Pois",
                column: "GoogleMapsUrl");

            migrationBuilder.CreateIndex(
                name: "IX_Pois_Latitude_Longitude",
                table: "Pois",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Pois_Name",
                table: "Pois",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Pois_Status",
                table: "Pois",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PoiTags_TagId",
                table: "PoiTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoiCollectionItems");

            migrationBuilder.DropTable(
                name: "PoiTags");

            migrationBuilder.DropTable(
                name: "PoiCollections");

            migrationBuilder.DropTable(
                name: "Pois");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
