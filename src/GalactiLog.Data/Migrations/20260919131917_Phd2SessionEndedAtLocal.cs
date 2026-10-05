using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phd2SessionEndedAtLocal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ended_at_local",
                table: "phd2_sessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ended_at_local",
                table: "phd2_sessions");
        }
    }
}
