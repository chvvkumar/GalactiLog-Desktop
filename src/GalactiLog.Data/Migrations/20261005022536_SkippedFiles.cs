using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class SkippedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skipped_files",
                columns: table => new
                {
                    file_path = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    file_size = table.Column<long>(type: "INTEGER", nullable: false),
                    file_mtime = table.Column<double>(type: "REAL", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skipped_files", x => x.file_path);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skipped_files");
        }
    }
}
