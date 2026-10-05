using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mosaics : Migration
    {
        private const string UniqueSessionIndexSql = """
            CREATE UNIQUE INDEX "ux_mosaic_panel_sessions_panel_target_date_label" ON "mosaic_panel_sessions" (
              "panel_id",
              "target_id",
              "session_date",
              coalesce("frame_label", '') COLLATE NOCASE
            );
            """;

        // The table of migration 0006, plus the mosaic foreign key when asked, then its three
        // fluent indexes. The column list is the table's whole, in its stored order.
        private static string RebuildCustomValuesSql(bool withMosaicKey) => $"""
            CREATE TABLE "ef_temp_custom_column_values" (
                "id" TEXT NOT NULL CONSTRAINT "PK_custom_column_values" PRIMARY KEY,
                "column_id" TEXT NOT NULL,
                "target_id" TEXT NULL,
                "mosaic_id" TEXT NULL,
                "session_date" TEXT NULL,
                "rig_label" TEXT NULL,
                "value" TEXT NOT NULL,
                "updated_at" TEXT NOT NULL,
                CONSTRAINT "FK_custom_column_values_custom_columns_column_id" FOREIGN KEY ("column_id") REFERENCES "custom_columns" ("id") ON DELETE CASCADE,
                {(withMosaicKey ? MosaicKeyConstraint : "")}
                CONSTRAINT "FK_custom_column_values_targets_target_id" FOREIGN KEY ("target_id") REFERENCES "targets" ("id") ON DELETE CASCADE
            );
            INSERT INTO "ef_temp_custom_column_values" ("id", "column_id", "target_id", "mosaic_id", "session_date", "rig_label", "value", "updated_at")
            SELECT "id", "column_id", "target_id", "mosaic_id", "session_date", "rig_label", "value", "updated_at"
            FROM "custom_column_values";
            DROP TABLE "custom_column_values";
            ALTER TABLE "ef_temp_custom_column_values" RENAME TO "custom_column_values";
            CREATE INDEX "ix_custom_column_values_column" ON "custom_column_values" ("column_id");
            CREATE INDEX "ix_custom_column_values_mosaic" ON "custom_column_values" ("mosaic_id");
            CREATE INDEX "ix_custom_column_values_target" ON "custom_column_values" ("target_id");
            """;

        private const string MosaicKeyConstraint =
            "CONSTRAINT \"FK_custom_column_values_mosaics_mosaic_id\" FOREIGN KEY (\"mosaic_id\") REFERENCES \"mosaics\" (\"id\") ON DELETE CASCADE,";

        private const string DropCustomValueIndexSql = """DROP INDEX IF EXISTS "uq_custom_column_value";""";

        // The expression of migration 0006, verbatim (spec 5.20).
        private const string CreateCustomValueIndexSql = """
            CREATE UNIQUE INDEX "uq_custom_column_value" ON "custom_column_values" (
              "column_id",
              coalesce("target_id", ''),
              coalesce("mosaic_id", ''),
              coalesce("session_date", ''),
              coalesce("rig_label", '')
            );
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "dec_deg",
                table: "images",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "panel_label",
                table: "images",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ra_deg",
                table: "images",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width_px",
                table: "images",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mosaic_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suggested_name = table.Column<string>(type: "TEXT", nullable: false),
                    base_name = table.Column<string>(type: "TEXT", nullable: false),
                    target_ids = table.Column<string>(type: "TEXT", nullable: false),
                    panel_labels = table.Column<string>(type: "TEXT", nullable: false),
                    panel_patterns = table.Column<string>(type: "TEXT", nullable: false),
                    session_dates = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    confidence = table.Column<string>(type: "TEXT", nullable: false),
                    discovery_source = table.Column<string>(type: "TEXT", nullable: false),
                    geometry = table.Column<string>(type: "TEXT", nullable: true),
                    flags = table.Column<string>(type: "TEXT", nullable: false),
                    dedup_signature = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mosaic_suggestions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mosaics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    rotation_angle = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mosaics", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mosaic_panels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    mosaic_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    panel_label = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    canvas_x = table.Column<double>(type: "REAL", nullable: true),
                    canvas_y = table.Column<double>(type: "REAL", nullable: true),
                    rotation = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    flip_h = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mosaic_panels", x => x.id);
                    table.CheckConstraint("CK_mosaic_panels_rotation", "rotation IN (0, 90, 180, 270)");
                    table.ForeignKey(
                        name: "FK_mosaic_panels_mosaics_mosaic_id",
                        column: x => x.mosaic_id,
                        principalTable: "mosaics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mosaic_panel_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    panel_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    frame_label = table.Column<string>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mosaic_panel_sessions", x => x.id);
                    table.CheckConstraint("CK_mosaic_panel_sessions_status", "status IN ('included', 'available')");
                    table.ForeignKey(
                        name: "FK_mosaic_panel_sessions_mosaic_panels_panel_id",
                        column: x => x.panel_id,
                        principalTable: "mosaic_panels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mosaic_panel_sessions_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_images_panel_label",
                table: "images",
                column: "panel_label");

            migrationBuilder.CreateIndex(
                name: "ix_mosaic_panel_sessions_target_date",
                table: "mosaic_panel_sessions",
                columns: new[] { "target_id", "session_date" });

            // ux_mosaic_panel_sessions_panel_target_date_label (spec 5.24). Raw SQL, because the
            // fluent model cannot carry the expression: SQLite treats two nulls as distinct in a
            // unique index, so the null frame label is folded to the empty string inside the
            // index, compared case insensitively, the way uq_custom_column_value folds its unset
            // key parts (spec 5.20).
            migrationBuilder.Sql(UniqueSessionIndexSql);

            migrationBuilder.CreateIndex(
                name: "ux_mosaic_panels_mosaic_label",
                table: "mosaic_panels",
                columns: new[] { "mosaic_id", "panel_label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mosaic_suggestions_dedup_signature",
                table: "mosaic_suggestions",
                column: "dedup_signature");

            migrationBuilder.CreateIndex(
                name: "ix_mosaic_suggestions_status",
                table: "mosaic_suggestions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_mosaics_name",
                table: "mosaics",
                column: "name",
                unique: true);

            // SQLite cannot add a foreign key to an existing table, so custom_column_values is
            // rebuilt (spec 5.25's migration note). By hand rather than through AddForeignKey: EF
            // defers its own rebuild to the end of the migration, after any Sql step, so the raw
            // SQL index of spec 5.20 could not be recreated after it. The table is a child only,
            // so the drop runs with foreign keys on and inside the migration's transaction.
            migrationBuilder.Sql(DropCustomValueIndexSql);
            migrationBuilder.Sql(RebuildCustomValuesSql(withMosaicKey: true));
            migrationBuilder.Sql(CreateCustomValueIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A mosaic-scope value has no row to go to once the mosaic key is gone, so it goes
            // first; then the reverse of Up's rebuild, before the mosaics table goes, so dropping
            // it cannot cascade into the values.
            migrationBuilder.Sql("""DELETE FROM "custom_column_values" WHERE "mosaic_id" IS NOT NULL;""");
            migrationBuilder.Sql(DropCustomValueIndexSql);
            migrationBuilder.Sql(RebuildCustomValuesSql(withMosaicKey: false));
            migrationBuilder.Sql(CreateCustomValueIndexSql);

            // Dropping the table would drop this index too. Dropped explicitly so a reader of
            // `Down` sees the whole of what `Up` made.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "ux_mosaic_panel_sessions_panel_target_date_label";""");

            migrationBuilder.DropTable(
                name: "mosaic_panel_sessions");

            migrationBuilder.DropTable(
                name: "mosaic_suggestions");

            migrationBuilder.DropTable(
                name: "mosaic_panels");

            migrationBuilder.DropTable(
                name: "mosaics");

            migrationBuilder.DropIndex(
                name: "ix_images_panel_label",
                table: "images");

            migrationBuilder.DropColumn(
                name: "dec_deg",
                table: "images");

            migrationBuilder.DropColumn(
                name: "panel_label",
                table: "images");

            migrationBuilder.DropColumn(
                name: "ra_deg",
                table: "images");

            migrationBuilder.DropColumn(
                name: "width_px",
                table: "images");
        }
    }
}
