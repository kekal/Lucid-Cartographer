using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidCartographer.Migrations
{
    /// <inheritdoc />
    public partial class AddOutgoingTravelMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutgoingTravelMode",
                table: "PoiCollectionItems",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PoiCollectionItem_OutgoingTravelMode",
                table: "PoiCollectionItems",
                sql: "OutgoingTravelMode IS NULL OR OutgoingTravelMode IN ('AnyAir','Drive','Walk','Cycle')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PoiCollectionItem_OutgoingTravelMode",
                table: "PoiCollectionItems");

            migrationBuilder.DropColumn(
                name: "OutgoingTravelMode",
                table: "PoiCollectionItems");
        }
    }
}
