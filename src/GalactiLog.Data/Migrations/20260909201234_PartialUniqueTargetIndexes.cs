using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartialUniqueTargetIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_targets_catalog_id_normalized",
                table: "targets");

            migrationBuilder.DropIndex(
                name: "IX_targets_primary_name",
                table: "targets");

            migrationBuilder.CreateIndex(
                name: "IX_targets_catalog_id_normalized",
                table: "targets",
                column: "catalog_id_normalized",
                unique: true,
                filter: "catalog_id_normalized IS NOT NULL AND merged_into_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_targets_primary_name",
                table: "targets",
                column: "primary_name",
                unique: true,
                filter: "merged_into_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_targets_catalog_id_normalized",
                table: "targets");

            migrationBuilder.DropIndex(
                name: "IX_targets_primary_name",
                table: "targets");

            migrationBuilder.CreateIndex(
                name: "IX_targets_catalog_id_normalized",
                table: "targets",
                column: "catalog_id_normalized",
                unique: true,
                filter: "catalog_id_normalized IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_targets_primary_name",
                table: "targets",
                column: "primary_name",
                unique: true);
        }
    }
}
