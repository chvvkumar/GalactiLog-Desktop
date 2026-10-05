using Microsoft.EntityFrameworkCore;
using GalactiLog.Data.Entities;

namespace GalactiLog.Data;

public sealed class GalactiLogContext : DbContext
{
    public GalactiLogContext(DbContextOptions<GalactiLogContext> options) : base(options) { }

    public DbSet<Image> Images => Set<Image>();
    public DbSet<Target> Targets => Set<Target>();
    public DbSet<TargetCatalogMembership> TargetCatalogMemberships => Set<TargetCatalogMembership>();
    public DbSet<OpenNgcCatalogEntry> OpenNgcCatalogEntries => Set<OpenNgcCatalogEntry>();
    public DbSet<StaticCatalogEntry> StaticCatalogEntries => Set<StaticCatalogEntry>();
    public DbSet<CatalogCacheEntry> CatalogCacheEntries => Set<CatalogCacheEntry>();
    public DbSet<UserSettingsRow> UserSettings => Set<UserSettingsRow>();
    public DbSet<SessionNote> SessionNotes => Set<SessionNote>();
    public DbSet<MergeCandidate> MergeCandidates => Set<MergeCandidate>();
    public DbSet<MergeManifest> MergeManifests => Set<MergeManifest>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<ScanRun> ScanRuns => Set<ScanRun>();
    public DbSet<Phd2Log> Phd2Logs => Set<Phd2Log>();
    public DbSet<Phd2Session> Phd2Sessions => Set<Phd2Session>();
    public DbSet<Phd2Frame> Phd2Frames => Set<Phd2Frame>();
    public DbSet<Phd2Calibration> Phd2Calibrations => Set<Phd2Calibration>();
    public DbSet<CustomColumn> CustomColumns => Set<CustomColumn>();
    public DbSet<CustomColumnValue> CustomColumnValues => Set<CustomColumnValue>();
    public DbSet<SkippedFile> SkippedFiles => Set<SkippedFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Image
        modelBuilder.Entity<Image>(e =>
        {
            // COLLATE NOCASE (migration 0003, phase 4 review item 1). Windows paths are
            // case-insensitive, and both the scan's known-file map and OrphanPruner key
            // file_path OrdinalIgnoreCase. With a BINARY column the writer's upsert lookup
            // would disagree with them: a row catalogued as C:\A\X.FITS would not be found
            // for C:\A\x.fits, and the scan would insert a duplicate. The unique index
            // inherits the column collation, so the database rejects that duplicate too.
            e.Property(x => x.FilePath).UseCollation("NOCASE");
            e.HasIndex(x => x.FilePath).IsUnique();
            e.HasIndex(x => x.CaptureDate);
            e.HasIndex(x => x.SessionDate);
            e.HasIndex(x => x.FilterUsed);
            e.HasIndex(x => x.ResolvedTargetId);
            e.HasIndex(x => x.ImageType);
            e.HasIndex(x => new { x.ImageType, x.SessionDate });
            e.HasIndex(x => x.Telescope);
            e.HasIndex(x => x.Camera);
            e.HasIndex(x => x.MedianHfr);
            e.HasIndex(x => x.Fwhm);
            e.HasIndex(x => x.Eccentricity);
            e.HasIndex(x => x.DetectedStars);
            e.HasIndex(x => x.GuidingRmsArcsec);
            e.HasIndex(x => x.FocuserTemp);
            e.HasIndex(x => x.AmbientTemp);
            e.HasIndex(x => x.Humidity);
            e.HasIndex(x => x.Airmass);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.ResolvedTargetId).OnDelete(DeleteBehavior.SetNull);
        });

        // Target
        modelBuilder.Entity<Target>(e =>
        {
            // Both unique indexes are PARTIAL on merged_into_id IS NULL (spec 5.3): a
            // merged-away target keeps its own primary_name and catalog_id_normalized, so a
            // full unique index would let a dead row block re-creating an active target with
            // the same designation. Phase 7's merge can then absorb a loser's names without
            // the resolver needing to know about merges at all (review ruling, item 13).
            e.HasIndex(x => x.PrimaryName).IsUnique().HasFilter("merged_into_id IS NULL");
            e.HasIndex(x => x.CatalogIdNormalized).IsUnique().HasFilter("catalog_id_normalized IS NOT NULL AND merged_into_id IS NULL");
            e.HasIndex(x => x.MergedIntoId);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.MergedIntoId).OnDelete(DeleteBehavior.Restrict);
        });

        // TargetCatalogMembership
        modelBuilder.Entity<TargetCatalogMembership>(e =>
        {
            e.HasIndex(x => new { x.TargetId, x.CatalogName }).IsUnique();
            e.HasIndex(x => x.TargetId);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        // OpenNgcCatalogEntry
        modelBuilder.Entity<OpenNgcCatalogEntry>(e =>
        {
            e.HasKey(x => x.Name);
            e.HasIndex(x => x.Messier);
        });

        // StaticCatalogEntry
        modelBuilder.Entity<StaticCatalogEntry>(e =>
        {
            e.HasKey(x => new { x.CatalogName, x.CatalogNumber });
            e.HasIndex(x => new { x.CatalogName, x.NgcName });
        });

        // CatalogCacheEntry
        modelBuilder.Entity<CatalogCacheEntry>(e =>
        {
            e.HasKey(x => new { x.Source, x.Key });
        });

        // UserSettingsRow
        modelBuilder.Entity<UserSettingsRow>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("user_settings", t => t.HasCheckConstraint("CK_user_settings_id", "id = 1"));
        });

        // SessionNote
        modelBuilder.Entity<SessionNote>(e =>
        {
            e.HasIndex(x => new { x.TargetId, x.SessionDate }).IsUnique();
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        // MergeCandidate
        modelBuilder.Entity<MergeCandidate>(e =>
        {
            e.HasIndex(x => x.Status);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.SuggestedTargetId).OnDelete(DeleteBehavior.SetNull);
        });

        // MergeManifest
        modelBuilder.Entity<MergeManifest>(e =>
        {
            e.HasIndex(x => x.WinnerId);
            e.HasIndex(x => x.LoserId);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.WinnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.LoserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ActivityEvent
        modelBuilder.Entity<ActivityEvent>(e =>
        {
            e.HasIndex(x => x.Timestamp).IsDescending();
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.ParentId);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ActivityEvent>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        });

        // ScanRun
        modelBuilder.Entity<ScanRun>(e =>
        {
            e.HasIndex(x => x.StartedAt).IsDescending();

            // Spec 5.13's closing paragraph: a database default, not only a CLR one, so an INSERT
            // written by an earlier version (or by hand) against these non-null columns succeeds
            // and reads as a run that found no guide logs.
            e.Property(x => x.Phd2Found).HasDefaultValue(0);
            e.Property(x => x.Phd2Ingested).HasDefaultValue(0);
            e.Property(x => x.Phd2Failed).HasDefaultValue(0);
        });

        // Phd2Log (spec 5.15)
        modelBuilder.Entity<Phd2Log>(e =>
        {
            // COLLATE NOCASE, for the reason the Image block above states at length: Windows paths
            // are case-insensitive and the guide-log pass keys its stored-path set
            // OrdinalIgnoreCase. With a BINARY column the delta skip of spec 10.3 would miss a
            // case-only variant and re-ingest the log on every scan, and the orphan drop would read
            // the stored row as missing and delete it.
            e.Property(x => x.FilePath).UseCollation("NOCASE");

            // Named rather than left to EF's IX_ convention, so this index is spelled like the nine
            // spec 5.16 to 5.18 name and the Diagnostics size probe reaches it with the same
            // `ix_phd2%` pattern it uses for them. It is the pattern that does the covering, not the
            // prefix `phd2`: no index name begins with that (review P2-1). See
            // DiagnosticsQuery.Phd2NameFilter.
            e.HasIndex(x => x.FilePath).IsUnique().HasDatabaseName("ix_phd2_logs_file_path");
        });

        // Phd2Session (spec 5.16). Four indexes, exactly the ones the spec names, by its names:
        // spec 7.6's correlation range scan is served by ix_phd2_sessions_started_at_utc by name.
        modelBuilder.Entity<Phd2Session>(e =>
        {
            e.HasIndex(x => x.SessionDate).HasDatabaseName("ix_phd2_sessions_session_date");
            e.HasIndex(x => new { x.Telescope, x.SessionDate })
                .HasDatabaseName("ix_phd2_sessions_telescope_session_date");
            e.HasIndex(x => x.StartedAtUtc).HasDatabaseName("ix_phd2_sessions_started_at_utc");
            e.HasIndex(x => x.LogId).HasDatabaseName("ix_phd2_sessions_log_id");
            e.HasOne<Phd2Log>().WithMany().HasForeignKey(x => x.LogId).OnDelete(DeleteBehavior.Cascade);
        });

        // Phd2Frame (spec 5.17). Two indexes, exactly the ones the spec names.
        modelBuilder.Entity<Phd2Frame>(e =>
        {
            e.HasIndex(x => new { x.SessionId, x.FrameIndex })
                .HasDatabaseName("ix_phd2_frames_session_frame");
            e.HasIndex(x => new { x.SessionId, x.TimeOffset })
                .HasDatabaseName("ix_phd2_frames_session_time");
            e.HasOne<Phd2Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        // Phd2Calibration (spec 5.18). Three indexes, exactly the ones the spec names.
        modelBuilder.Entity<Phd2Calibration>(e =>
        {
            e.HasIndex(x => x.LogId).HasDatabaseName("ix_phd2_calibrations_log_id");
            e.HasIndex(x => x.SessionDate).HasDatabaseName("ix_phd2_calibrations_session_date");
            e.HasIndex(x => x.StartedAtUtc).HasDatabaseName("ix_phd2_calibrations_started_at_utc");
            e.HasOne<Phd2Log>().WithMany().HasForeignKey(x => x.LogId).OnDelete(DeleteBehavior.Cascade);
        });

        // custom_columns (spec 5.19). Two indexes, exactly the ones the spec names.
        modelBuilder.Entity<CustomColumn>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_custom_columns_slug");
            e.HasIndex(x => x.DisplayOrder).HasDatabaseName("ix_custom_columns_display_order");
        });

        // custom_column_values (spec 5.20). Four indexes. display_order carries NO unique index:
        // SQLite cannot defer a constraint and the reorder swap would trip one mid-transaction.
        modelBuilder.Entity<CustomColumnValue>(e =>
        {
            e.HasIndex(x => x.TargetId).HasDatabaseName("ix_custom_column_values_target");
            e.HasIndex(x => x.ColumnId).HasDatabaseName("ix_custom_column_values_column");
            e.HasIndex(x => x.MosaicId).HasDatabaseName("ix_custom_column_values_mosaic");
            e.HasOne<CustomColumn>().WithMany().HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Target>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        // skipped_files (spec 5.21). COLLATE NOCASE for the reason the Image block states: the
        // known set and the writer's upsert lookup both compare paths case-insensitively.
        modelBuilder.Entity<SkippedFile>(e =>
        {
            e.Property(x => x.FilePath).UseCollation("NOCASE");
        });
    }
}
