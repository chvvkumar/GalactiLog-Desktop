using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_columns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    column_type = table.Column<string>(type: "TEXT", nullable: false),
                    applies_to = table.Column<string>(type: "TEXT", nullable: false),
                    dropdown_options = table.Column<string>(type: "TEXT", nullable: true),
                    display_order = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_columns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "custom_column_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    column_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    mosaic_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    session_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    rig_label = table.Column<string>(type: "TEXT", nullable: true),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_column_values", x => x.id);
                    table.ForeignKey(
                        name: "FK_custom_column_values_custom_columns_column_id",
                        column: x => x.column_id,
                        principalTable: "custom_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_custom_column_values_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_custom_column_values_column",
                table: "custom_column_values",
                column: "column_id");

            migrationBuilder.CreateIndex(
                name: "ix_custom_column_values_mosaic",
                table: "custom_column_values",
                column: "mosaic_id");

            migrationBuilder.CreateIndex(
                name: "ix_custom_column_values_target",
                table: "custom_column_values",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_custom_columns_display_order",
                table: "custom_columns",
                column: "display_order");

            migrationBuilder.CreateIndex(
                name: "ux_custom_columns_slug",
                table: "custom_columns",
                column: "slug",
                unique: true);

            // uq_custom_column_value (spec 5.20). Not declarable in the fluent model, because it
            // indexes four coalesce expressions: a scope's unset key parts are SQL null, and two
            // rows differing only in a null against an empty string would both be legal, so the
            // upsert would find neither and the surface would show whichever the reader returned.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "uq_custom_column_value" ON "custom_column_values" (
                  "column_id",
                  coalesce("target_id", ''),
                  coalesce("mosaic_id", ''),
                  coalesce("session_date", ''),
                  coalesce("rig_label", '')
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table would drop this index too. Dropped explicitly so a reader of
            // `Down` sees the whole of what `Up` made.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "uq_custom_column_value";""");

            migrationBuilder.DropTable(
                name: "custom_column_values");

            migrationBuilder.DropTable(
                name: "custom_columns");
        }
    }
}
