using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalactiLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_cache",
                columns: table => new
                {
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: true),
                    negative = table.Column<bool>(type: "INTEGER", nullable: false),
                    fetched_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_cache", x => new { x.source, x.key });
                });

            migrationBuilder.CreateTable(
                name: "openngc_catalog",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: true),
                    ra = table.Column<double>(type: "REAL", nullable: true),
                    dec = table.Column<double>(type: "REAL", nullable: true),
                    constellation = table.Column<string>(type: "TEXT", nullable: true),
                    major_axis = table.Column<double>(type: "REAL", nullable: true),
                    minor_axis = table.Column<double>(type: "REAL", nullable: true),
                    position_angle = table.Column<double>(type: "REAL", nullable: true),
                    b_mag = table.Column<double>(type: "REAL", nullable: true),
                    v_mag = table.Column<double>(type: "REAL", nullable: true),
                    surface_brightness = table.Column<double>(type: "REAL", nullable: true),
                    common_names = table.Column<string>(type: "TEXT", nullable: true),
                    messier = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_openngc_catalog", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "scan_runs",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    trigger = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    discovered = table.Column<int>(type: "INTEGER", nullable: false),
                    new_files = table.Column<int>(type: "INTEGER", nullable: false),
                    changed_files = table.Column<int>(type: "INTEGER", nullable: false),
                    completed = table.Column<int>(type: "INTEGER", nullable: false),
                    failed = table.Column<int>(type: "INTEGER", nullable: false),
                    skipped_calibration = table.Column<int>(type: "INTEGER", nullable: false),
                    removed = table.Column<int>(type: "INTEGER", nullable: false),
                    error_text = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "static_catalog_entries",
                columns: table => new
                {
                    catalog_name = table.Column<string>(type: "TEXT", nullable: false),
                    catalog_number = table.Column<string>(type: "TEXT", nullable: false),
                    ngc_name = table.Column<string>(type: "TEXT", nullable: true),
                    payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_static_catalog_entries", x => new { x.catalog_name, x.catalog_number });
                });

            migrationBuilder.CreateTable(
                name: "targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    primary_name = table.Column<string>(type: "TEXT", nullable: false),
                    catalog_id = table.Column<string>(type: "TEXT", nullable: true),
                    catalog_id_normalized = table.Column<string>(type: "TEXT", nullable: true),
                    common_name = table.Column<string>(type: "TEXT", nullable: true),
                    aliases = table.Column<string>(type: "TEXT", nullable: false),
                    ra = table.Column<double>(type: "REAL", nullable: true),
                    dec = table.Column<double>(type: "REAL", nullable: true),
                    object_type = table.Column<string>(type: "TEXT", nullable: true),
                    constellation = table.Column<string>(type: "TEXT", nullable: true),
                    size_major = table.Column<double>(type: "REAL", nullable: true),
                    size_minor = table.Column<double>(type: "REAL", nullable: true),
                    position_angle = table.Column<double>(type: "REAL", nullable: true),
                    v_mag = table.Column<double>(type: "REAL", nullable: true),
                    surface_brightness = table.Column<double>(type: "REAL", nullable: true),
                    sac_description = table.Column<string>(type: "TEXT", nullable: true),
                    sac_notes = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    reference_thumbnail_path = table.Column<string>(type: "TEXT", nullable: true),
                    merged_into_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    merged_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    name_locked = table.Column<bool>(type: "INTEGER", nullable: false),
                    user_defined = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_targets", x => x.id);
                    table.ForeignKey(
                        name: "FK_targets_targets_merged_into_id",
                        column: x => x.merged_into_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    general = table.Column<string>(type: "TEXT", nullable: false),
                    filters = table.Column<string>(type: "TEXT", nullable: false),
                    equipment = table.Column<string>(type: "TEXT", nullable: false),
                    dismissed_suggestions = table.Column<string>(type: "TEXT", nullable: false),
                    display = table.Column<string>(type: "TEXT", nullable: false),
                    graph = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_settings", x => x.id);
                    table.CheckConstraint("CK_user_settings_id", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "activity_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    category = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: false),
                    details = table.Column<string>(type: "TEXT", nullable: true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    parent_id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_activity_events_activity_events_parent_id",
                        column: x => x.parent_id,
                        principalTable: "activity_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_activity_events_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", nullable: false),
                    capture_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    session_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    thumbnail_path = table.Column<string>(type: "TEXT", nullable: true),
                    resolved_target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    exposure_time = table.Column<double>(type: "REAL", nullable: true),
                    filter_used = table.Column<string>(type: "TEXT", nullable: true),
                    sensor_temp = table.Column<double>(type: "REAL", nullable: true),
                    camera_gain = table.Column<int>(type: "INTEGER", nullable: true),
                    image_type = table.Column<string>(type: "TEXT", nullable: true),
                    telescope = table.Column<string>(type: "TEXT", nullable: true),
                    camera = table.Column<string>(type: "TEXT", nullable: true),
                    median_hfr = table.Column<double>(type: "REAL", nullable: true),
                    median_fwhm = table.Column<double>(type: "REAL", nullable: true),
                    eccentricity = table.Column<double>(type: "REAL", nullable: true),
                    eccentricity_source = table.Column<string>(type: "TEXT", nullable: true),
                    altitude_deg = table.Column<double>(type: "REAL", nullable: true),
                    arcsec_per_pixel = table.Column<double>(type: "REAL", nullable: true),
                    hfr_stdev = table.Column<double>(type: "REAL", nullable: true),
                    fwhm = table.Column<double>(type: "REAL", nullable: true),
                    detected_stars = table.Column<int>(type: "INTEGER", nullable: true),
                    guiding_rms_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    guiding_rms_ra_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    guiding_rms_dec_arcsec = table.Column<double>(type: "REAL", nullable: true),
                    guiding_rms_source = table.Column<string>(type: "TEXT", nullable: true),
                    adu_stdev = table.Column<double>(type: "REAL", nullable: true),
                    adu_mean = table.Column<double>(type: "REAL", nullable: true),
                    adu_median = table.Column<double>(type: "REAL", nullable: true),
                    adu_min = table.Column<int>(type: "INTEGER", nullable: true),
                    adu_max = table.Column<int>(type: "INTEGER", nullable: true),
                    focuser_position = table.Column<int>(type: "INTEGER", nullable: true),
                    focuser_temp = table.Column<double>(type: "REAL", nullable: true),
                    rotator_position = table.Column<double>(type: "REAL", nullable: true),
                    pier_side = table.Column<string>(type: "TEXT", nullable: true),
                    airmass = table.Column<double>(type: "REAL", nullable: true),
                    ambient_temp = table.Column<double>(type: "REAL", nullable: true),
                    dew_point = table.Column<double>(type: "REAL", nullable: true),
                    humidity = table.Column<double>(type: "REAL", nullable: true),
                    pressure = table.Column<double>(type: "REAL", nullable: true),
                    wind_speed = table.Column<double>(type: "REAL", nullable: true),
                    wind_direction = table.Column<double>(type: "REAL", nullable: true),
                    wind_gust = table.Column<double>(type: "REAL", nullable: true),
                    cloud_cover = table.Column<double>(type: "REAL", nullable: true),
                    sky_quality = table.Column<double>(type: "REAL", nullable: true),
                    file_size = table.Column<long>(type: "INTEGER", nullable: true),
                    file_mtime = table.Column<double>(type: "REAL", nullable: true),
                    raw_headers = table.Column<string>(type: "TEXT", nullable: true),
                    provenance = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_images", x => x.id);
                    table.ForeignKey(
                        name: "FK_images_targets_resolved_target_id",
                        column: x => x.resolved_target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "merge_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_name = table.Column<string>(type: "TEXT", nullable: false),
                    source_image_count = table.Column<int>(type: "INTEGER", nullable: false),
                    suggested_target_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    similarity_score = table.Column<double>(type: "REAL", nullable: false),
                    method = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    reason_text = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merge_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_merge_candidates_targets_suggested_target_id",
                        column: x => x.suggested_target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "merge_manifests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    winner_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    loser_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merge_manifests", x => x.id);
                    table.ForeignKey(
                        name: "FK_merge_manifests_targets_loser_id",
                        column: x => x.loser_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_merge_manifests_targets_winner_id",
                        column: x => x.winner_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    notes = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_notes", x => x.id);
                    table.ForeignKey(
                        name: "FK_session_notes_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "target_catalog_memberships",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    catalog_name = table.Column<string>(type: "TEXT", nullable: false),
                    catalog_number = table.Column<string>(type: "TEXT", nullable: false),
                    metadata = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_target_catalog_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_target_catalog_memberships_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_category",
                table: "activity_events",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_parent_id",
                table: "activity_events",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_target_id",
                table: "activity_events",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_timestamp",
                table: "activity_events",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_images_airmass",
                table: "images",
                column: "airmass");

            migrationBuilder.CreateIndex(
                name: "IX_images_ambient_temp",
                table: "images",
                column: "ambient_temp");

            migrationBuilder.CreateIndex(
                name: "IX_images_camera",
                table: "images",
                column: "camera");

            migrationBuilder.CreateIndex(
                name: "IX_images_capture_date",
                table: "images",
                column: "capture_date");

            migrationBuilder.CreateIndex(
                name: "IX_images_detected_stars",
                table: "images",
                column: "detected_stars");

            migrationBuilder.CreateIndex(
                name: "IX_images_eccentricity",
                table: "images",
                column: "eccentricity");

            migrationBuilder.CreateIndex(
                name: "IX_images_file_path",
                table: "images",
                column: "file_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_images_filter_used",
                table: "images",
                column: "filter_used");

            migrationBuilder.CreateIndex(
                name: "IX_images_focuser_temp",
                table: "images",
                column: "focuser_temp");

            migrationBuilder.CreateIndex(
                name: "IX_images_fwhm",
                table: "images",
                column: "fwhm");

            migrationBuilder.CreateIndex(
                name: "IX_images_guiding_rms_arcsec",
                table: "images",
                column: "guiding_rms_arcsec");

            migrationBuilder.CreateIndex(
                name: "IX_images_humidity",
                table: "images",
                column: "humidity");

            migrationBuilder.CreateIndex(
                name: "IX_images_image_type",
                table: "images",
                column: "image_type");

            migrationBuilder.CreateIndex(
                name: "IX_images_image_type_session_date",
                table: "images",
                columns: new[] { "image_type", "session_date" });

            migrationBuilder.CreateIndex(
                name: "IX_images_median_hfr",
                table: "images",
                column: "median_hfr");

            migrationBuilder.CreateIndex(
                name: "IX_images_resolved_target_id",
                table: "images",
                column: "resolved_target_id");

            migrationBuilder.CreateIndex(
                name: "IX_images_session_date",
                table: "images",
                column: "session_date");

            migrationBuilder.CreateIndex(
                name: "IX_images_telescope",
                table: "images",
                column: "telescope");

            migrationBuilder.CreateIndex(
                name: "IX_merge_candidates_status",
                table: "merge_candidates",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_merge_candidates_suggested_target_id",
                table: "merge_candidates",
                column: "suggested_target_id");

            migrationBuilder.CreateIndex(
                name: "IX_merge_manifests_loser_id",
                table: "merge_manifests",
                column: "loser_id");

            migrationBuilder.CreateIndex(
                name: "IX_merge_manifests_winner_id",
                table: "merge_manifests",
                column: "winner_id");

            migrationBuilder.CreateIndex(
                name: "IX_openngc_catalog_messier",
                table: "openngc_catalog",
                column: "messier");

            migrationBuilder.CreateIndex(
                name: "IX_scan_runs_started_at",
                table: "scan_runs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_session_notes_target_id_session_date",
                table: "session_notes",
                columns: new[] { "target_id", "session_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_static_catalog_entries_catalog_name_ngc_name",
                table: "static_catalog_entries",
                columns: new[] { "catalog_name", "ngc_name" });

            migrationBuilder.CreateIndex(
                name: "IX_target_catalog_memberships_target_id",
                table: "target_catalog_memberships",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_target_catalog_memberships_target_id_catalog_name",
                table: "target_catalog_memberships",
                columns: new[] { "target_id", "catalog_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_targets_catalog_id_normalized",
                table: "targets",
                column: "catalog_id_normalized",
                unique: true,
                filter: "catalog_id_normalized IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_targets_merged_into_id",
                table: "targets",
                column: "merged_into_id");

            migrationBuilder.CreateIndex(
                name: "IX_targets_primary_name",
                table: "targets",
                column: "primary_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_events");

            migrationBuilder.DropTable(
                name: "catalog_cache");

            migrationBuilder.DropTable(
                name: "images");

            migrationBuilder.DropTable(
                name: "merge_candidates");

            migrationBuilder.DropTable(
                name: "merge_manifests");

            migrationBuilder.DropTable(
                name: "openngc_catalog");

            migrationBuilder.DropTable(
                name: "scan_runs");

            migrationBuilder.DropTable(
                name: "session_notes");

            migrationBuilder.DropTable(
                name: "static_catalog_entries");

            migrationBuilder.DropTable(
                name: "target_catalog_memberships");

            migrationBuilder.DropTable(
                name: "user_settings");

            migrationBuilder.DropTable(
                name: "targets");
        }
    }
}
