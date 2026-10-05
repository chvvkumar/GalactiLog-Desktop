using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phd2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "phd2_failed",
                table: "scan_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "phd2_found",
                table: "scan_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "phd2_ingested",
                table: "scan_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "phd2_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    file_size = table.Column<long>(type: "INTEGER", nullable: true),
                    file_mtime = table.Column<double>(type: "REAL", nullable: true),
                    parse_status = table.Column<string>(type: "TEXT", nullable: false),
                    parse_error = table.Column<string>(type: "TEXT", nullable: true),
                    phd2_version = table.Column<string>(type: "TEXT", nullable: true),
                    log_version = table.Column<string>(type: "TEXT", nullable: true),
                    run_count = table.Column<int>(type: "INTEGER", nullable: false),
                    session_count = table.Column<int>(type: "INTEGER", nullable: false),
                    calibration_count = table.Column<int>(type: "INTEGER", nullable: false),
                    parsed_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phd2_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "phd2_calibrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    log_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_at_local = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    session_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    equipment_profile = table.Column<string>(type: "TEXT", nullable: true),
                    telescope = table.Column<string>(type: "TEXT", nullable: true),
                    pixel_scale_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    focal_length_mm = table.Column<double>(type: "REAL", nullable: true),
                    guide_camera = table.Column<string>(type: "TEXT", nullable: true),
                    mount_name = table.Column<string>(type: "TEXT", nullable: true),
                    ra_guide_speed = table.Column<double>(type: "REAL", nullable: true),
                    dec_guide_speed = table.Column<double>(type: "REAL", nullable: true),
                    dec_deg = table.Column<double>(type: "REAL", nullable: true),
                    hour_angle_hr = table.Column<double>(type: "REAL", nullable: true),
                    pier_side = table.Column<string>(type: "TEXT", nullable: true),
                    alt_deg = table.Column<double>(type: "REAL", nullable: true),
                    az_deg = table.Column<double>(type: "REAL", nullable: true),
                    west_angle_deg = table.Column<double>(type: "REAL", nullable: true),
                    west_rate_px_s = table.Column<double>(type: "REAL", nullable: true),
                    west_parity = table.Column<string>(type: "TEXT", nullable: true),
                    north_angle_deg = table.Column<double>(type: "REAL", nullable: true),
                    north_rate_px_s = table.Column<double>(type: "REAL", nullable: true),
                    north_parity = table.Column<string>(type: "TEXT", nullable: true),
                    completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    steps = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phd2_calibrations", x => x.id);
                    table.ForeignKey(
                        name: "FK_phd2_calibrations_phd2_logs_log_id",
                        column: x => x.log_id,
                        principalTable: "phd2_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "phd2_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    log_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_index = table.Column<int>(type: "INTEGER", nullable: false),
                    section_index = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at_local = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ended_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    duration_s = table.Column<double>(type: "REAL", nullable: false),
                    session_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    equipment_profile = table.Column<string>(type: "TEXT", nullable: true),
                    telescope = table.Column<string>(type: "TEXT", nullable: true),
                    pixel_scale_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    focal_length_mm = table.Column<double>(type: "REAL", nullable: true),
                    guide_camera = table.Column<string>(type: "TEXT", nullable: true),
                    exposure_ms = table.Column<double>(type: "REAL", nullable: true),
                    mount_name = table.Column<string>(type: "TEXT", nullable: true),
                    dec_guide_mode = table.Column<string>(type: "TEXT", nullable: true),
                    algo_ra = table.Column<string>(type: "TEXT", nullable: true),
                    algo_dec = table.Column<string>(type: "TEXT", nullable: true),
                    min_move_ra = table.Column<double>(type: "REAL", nullable: true),
                    min_move_dec = table.Column<double>(type: "REAL", nullable: true),
                    aggression_ra = table.Column<double>(type: "REAL", nullable: true),
                    ortho_error_deg = table.Column<double>(type: "REAL", nullable: true),
                    last_cal_issue = table.Column<string>(type: "TEXT", nullable: true),
                    pier_side = table.Column<string>(type: "TEXT", nullable: true),
                    alt_deg = table.Column<double>(type: "REAL", nullable: true),
                    az_deg = table.Column<double>(type: "REAL", nullable: true),
                    dec_deg = table.Column<double>(type: "REAL", nullable: true),
                    hour_angle_hr = table.Column<double>(type: "REAL", nullable: true),
                    frame_count = table.Column<int>(type: "INTEGER", nullable: false),
                    drop_count = table.Column<int>(type: "INTEGER", nullable: false),
                    max_drop_run = table.Column<int>(type: "INTEGER", nullable: false),
                    unguided_seconds = table.Column<double>(type: "REAL", nullable: false),
                    rms_ra_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    rms_dec_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    rms_total_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    rms_ra_filtered_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    rms_dec_filtered_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    rms_total_filtered_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    peak_ra_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    peak_dec_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    snr_mean = table.Column<double>(type: "REAL", nullable: true),
                    snr_min = table.Column<double>(type: "REAL", nullable: true),
                    star_mass_mean = table.Column<double>(type: "REAL", nullable: true),
                    pulse_count_ra_west = table.Column<int>(type: "INTEGER", nullable: false),
                    pulse_count_ra_east = table.Column<int>(type: "INTEGER", nullable: false),
                    pulse_count_dec_north = table.Column<int>(type: "INTEGER", nullable: false),
                    pulse_count_dec_south = table.Column<int>(type: "INTEGER", nullable: false),
                    pulse_total_ms_ra = table.Column<int>(type: "INTEGER", nullable: false),
                    pulse_total_ms_dec = table.Column<int>(type: "INTEGER", nullable: false),
                    dither_count = table.Column<int>(type: "INTEGER", nullable: false),
                    settle_count = table.Column<int>(type: "INTEGER", nullable: false),
                    settle_failed_count = table.Column<int>(type: "INTEGER", nullable: false),
                    settle_median_s = table.Column<double>(type: "REAL", nullable: true),
                    star_lost_reasons = table.Column<string>(type: "TEXT", nullable: false),
                    events = table.Column<string>(type: "TEXT", nullable: false),
                    truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    discarded_rows = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phd2_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_phd2_sessions_phd2_logs_log_id",
                        column: x => x.log_id,
                        principalTable: "phd2_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "phd2_frames",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    frame_index = table.Column<int>(type: "INTEGER", nullable: false),
                    time_offset = table.Column<double>(type: "REAL", nullable: false),
                    dx = table.Column<double>(type: "REAL", nullable: true),
                    dy = table.Column<double>(type: "REAL", nullable: true),
                    ra_raw = table.Column<double>(type: "REAL", nullable: true),
                    dec_raw = table.Column<double>(type: "REAL", nullable: true),
                    ra_guide = table.Column<double>(type: "REAL", nullable: true),
                    dec_guide = table.Column<double>(type: "REAL", nullable: true),
                    ra_duration_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    ra_direction = table.Column<string>(type: "TEXT", nullable: false),
                    dec_duration_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    dec_direction = table.Column<string>(type: "TEXT", nullable: false),
                    star_mass = table.Column<double>(type: "REAL", nullable: true),
                    snr = table.Column<double>(type: "REAL", nullable: true),
                    error_code = table.Column<int>(type: "INTEGER", nullable: true),
                    dropped = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phd2_frames", x => x.id);
                    table.ForeignKey(
                        name: "FK_phd2_frames_phd2_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "phd2_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_phd2_calibrations_log_id",
                table: "phd2_calibrations",
                column: "log_id");

            migrationBuilder.CreateIndex(
                name: "ix_phd2_calibrations_session_date",
                table: "phd2_calibrations",
                column: "session_date");

            migrationBuilder.CreateIndex(
                name: "ix_phd2_calibrations_started_at_utc",
                table: "phd2_calibrations",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_phd2_frames_session_frame",
                table: "phd2_frames",
                columns: new[] { "session_id", "frame_index" });

            migrationBuilder.CreateIndex(
                name: "ix_phd2_frames_session_time",
                table: "phd2_frames",
                columns: new[] { "session_id", "time_offset" });

            migrationBuilder.CreateIndex(
                name: "ix_phd2_logs_file_path",
                table: "phd2_logs",
                column: "file_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_phd2_sessions_log_id",
                table: "phd2_sessions",
                column: "log_id");

            migrationBuilder.CreateIndex(
                name: "ix_phd2_sessions_session_date",
                table: "phd2_sessions",
                column: "session_date");

            migrationBuilder.CreateIndex(
                name: "ix_phd2_sessions_started_at_utc",
                table: "phd2_sessions",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_phd2_sessions_telescope_session_date",
                table: "phd2_sessions",
                columns: new[] { "telescope", "session_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "phd2_calibrations");

            migrationBuilder.DropTable(
                name: "phd2_frames");

            migrationBuilder.DropTable(
                name: "phd2_sessions");

            migrationBuilder.DropTable(
                name: "phd2_logs");

            migrationBuilder.DropColumn(
                name: "phd2_failed",
                table: "scan_runs");

            migrationBuilder.DropColumn(
                name: "phd2_found",
                table: "scan_runs");

            migrationBuilder.DropColumn(
                name: "phd2_ingested",
                table: "scan_runs");
        }
    }
}
