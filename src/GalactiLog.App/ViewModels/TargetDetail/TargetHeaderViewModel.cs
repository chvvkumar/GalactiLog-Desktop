using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 12.4's header block as a display projection of <see cref="TargetHeaderBlock"/>, in the
/// shape <c>TargetRowViewModel</c> already established. Formatting only: no business logic, no
/// queries, no filesystem. Every field has a <c>Has*</c> companion so the view binds
/// <c>IsVisible</c> and needs no converters.
/// </summary>
public sealed partial class TargetHeaderViewModel : ObservableObject
{
    /// <param name="block">The loaded header read model.</param>
    /// <param name="createReferenceSlot">Builds the reference thumbnail slot from the stored
    /// cache-relative path, already loaded (Phase 8 Task 6). Optional and trailing, so nothing
    /// existing breaks; null leaves <see cref="ReferenceThumbnail"/> null and the view shows
    /// spec 12.10's placeholder. The slot's lifetime is the page's:
    /// <c>TargetDetailViewModel</c> disposes it on reload and on close.</param>
    /// <param name="setObjectType">Spec 12.4's object type edit (PAR-009), normally
    /// <c>TargetWriteRepository.SetObjectType</c>. Optional and trailing, so no existing
    /// construction site changes; null leaves the pencil on screen for a resolved target and makes
    /// its commit write nothing, which is what every test that is not about the edit wants.</param>
    public TargetHeaderViewModel(
        TargetHeaderBlock block,
        Func<string, ThumbnailSlotViewModel>? createReferenceSlot = null,
        Action<Guid, string>? setObjectType = null)
    {
        Block = block;
        Name = block.PrimaryName;
        NameLocked = block.NameLocked;
        ObjectType = block.ObjectType ?? "";
        ObjectCategory = block.ObjectCategory;
        _setObjectType = setObjectType;

        Aliases = block.Aliases;
        CatalogMemberships = block.CatalogMemberships;
        RaText = FormatRightAscension(block.Ra);
        DecText = FormatDeclination(block.Dec);
        SizeText = FormatSize(block.SizeMajor, block.SizeMinor);
        PositionAngleText = MetricText.Format(block.PositionAngle, "0", " deg");
        VMagText = MetricText.Format(block.VMag, "0.00");
        SurfaceBrightnessText = MetricText.Format(block.SurfaceBrightness, "0.0");

        var (scaleBarWidth, scaleBarText) = ScaleBar(
            block.ReferenceArcsecPerPixel,
            block.ReferenceFrameWidthPixels);
        ScaleBarWidth = scaleBarWidth;
        ScaleBarText = scaleBarText;

        // Built here rather than by the view, so the page owns the slot's lifetime and the view
        // stays a projection. The factory loads it; a target with no stored path gets no slot at
        // all, which is the placeholder case.
        if (HasReferenceThumbnail && createReferenceSlot is not null)
        {
            ReferenceThumbnail = createReferenceSlot(block.ReferenceThumbnailPath!);
        }
    }

    /// <summary>The read model behind the block. The page reads <c>Notes</c> and <c>TargetId</c>
    /// off it rather than duplicating them as display strings.</summary>
    public TargetHeaderBlock Block { get; }

    /// <summary>The storage form of the dashboard's group key (Task 1 handoff), equal by ordinary
    /// ordinal comparison to <c>TargetRow.GroupKey</c>.</summary>
    public string GroupKey => Block.GroupKey;

    /// <summary>Null for an <c>obj:</c> group, which has no targets row. Rename, re-resolve and
    /// target notes all need it, so those actions are disabled when it is null.</summary>
    public Guid? TargetId => Block.TargetId;

    /// <summary>The primary name, or the raw <c>OBJECT</c> string for an <c>obj:</c> group.
    /// Observable because a successful rename sets it without a reload.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Spec 5.3's <c>name_locked</c>: set when the user renames a target, so later
    /// catalog enrichment leaves the name alone. Observable for the same reason as
    /// <see cref="Name"/>.</summary>
    [ObservableProperty]
    public partial bool NameLocked { get; set; }

    public IReadOnlyList<string> Aliases { get; }

    public bool HasAliases => Aliases.Count > 0;

    /// <summary>The object's other names on one line, for the identity line beside the title
    /// (P12). The drawer still lists them one tag each.</summary>
    public string AliasesText => string.Join(", ", Aliases);

    /// <summary>The stored <c>object_type</c>, which is SIMBAD's raw OTYPELIST (spec 9.8).
    /// Observable because spec 12.4's edit shows the new value the moment the write returns rather
    /// than waiting for the next scan-driven reload.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObjectType), nameof(ShowObjectTypeRow))]
    public partial string ObjectType { get; private set; }

    public bool HasObjectType => !string.IsNullOrWhiteSpace(ObjectType);

    /// <summary>
    /// Whether the Details panel draws the object type row at all: whenever there is a value to
    /// read, and whenever the target can be given one.
    /// </summary>
    /// <remarks>
    /// The row was bound to <see cref="HasObjectType"/> alone, which hid the pencil in the two
    /// cases that need it most: a resolved target whose <c>object_type</c> is null, and a target
    /// the user has just set to <c>Other</c>, which stores the empty string (spec 9.8: "anything
    /// mapped by no code is Other"). An edit the user cannot undo is worse than an empty value
    /// beside a label. An <c>obj:</c> group still shows no row, because it has neither.
    /// </remarks>
    public bool ShowObjectTypeRow => HasObjectType || CanEditObjectType;

    /// <summary>Never null or empty (Task 1 handoff): an unresolved group reports the Unresolved
    /// category rather than nothing. Observable for the same reason as
    /// <see cref="ObjectType"/>.</summary>
    [ObservableProperty]
    public partial string ObjectCategory { get; private set; }

    // ---- P14A Task 6: spec 12.4's object type edit (PAR-009) ---------------------------------

    // Normally TargetWriteRepository.SetObjectType with its outcome discarded. Null leaves the
    // pencil on screen for a resolved target and makes its commit write nothing and change
    // nothing on screen: the displayed value follows the write and never leads it (phase review
    // P3-5).
    private readonly Action<Guid, string>? _setObjectType;

    /// <summary>
    /// Whether spec 12.4's pencil is offered: on a resolved target only. An <c>obj:</c> group is
    /// an unresolved <c>OBJECT</c> string with no <c>targets</c> row to write, so the pencil is
    /// absent rather than disabled.
    /// </summary>
    public bool CanEditObjectType => Block.TargetId is not null;

    /// <summary>
    /// Spec 12.4's combo box entries: section 9.8's nine display categories in that table's own
    /// order, followed by <c>Other</c>.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="ObjectTypeCategories.DisplayCategories"/> rather than retyped, so this
    /// list and the dashboard's Object Type pills cannot drift. <c>Unresolved</c> is withheld
    /// because it describes a frame with no target rather than a target (spec 12.4). The five
    /// solar system categories are withheld too (task1-report open question 6, ruled as proposed):
    /// they belong to <c>user_defined</c> targets the name-pattern classifier of spec 9.2 created,
    /// and a user who needs one already has a target the classifier categorised.
    /// </remarks>
    public IReadOnlyList<string> ObjectTypeChoices => Choices;

    private static readonly string[] Choices =
        [.. ObjectTypeCategories.DisplayCategories, TargetWriteRepository.OtherCategory];

    /// <summary>Whether the combo box has replaced the read-only value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeginEditObjectType))]
    public partial bool IsEditingObjectType { get; private set; }

    /// <summary>Whether the pencil is on screen: on a resolved target, and not while its own
    /// editor is open.</summary>
    /// <remarks>Task 6 review P3: the pencil bound <see cref="CanEditObjectType"/> alone, so it
    /// stayed beside the combo box it had just opened, offering to open an editor that was already
    /// there. This is the same not-editing term the read-only value beside it uses.</remarks>
    public bool CanBeginEditObjectType => CanEditObjectType && !IsEditingObjectType;

    /// <summary>The combo box's selection. Writing it while <see cref="IsEditingObjectType"/> is
    /// set commits, which is spec 12.4's "choosing an entry commits at once".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitObjectTypeCommand))]
    public partial string? SelectedObjectTypeChoice { get; set; }

    partial void OnSelectedObjectTypeChoiceChanged(string? value)
    {
        // Spec 12.4: choosing an entry commits at once and the box swaps back to the read-only
        // value. BeginEdit seeds the selection BEFORE it opens the box, so seeding is not a
        // commit; Cancel closes the box before it clears the selection, for the same reason.
        if (IsEditingObjectType)
        {
            CommitObjectTypeCommand.Execute(null);
        }
    }

    /// <summary>Swaps the read-only value for the combo box, showing the current category as the
    /// box's placeholder.</summary>
    /// <remarks>
    /// The box opens unseeded (Task 6 review P3). It used to open on the current category, which
    /// read well and behaved badly: choosing the entry already shown is not a change, so
    /// <see cref="OnSelectedObjectTypeChoiceChanged"/> never ran, nothing committed and the box
    /// stayed on screen looking stuck. Unseeded, every entry in the list is a selection, and the
    /// current category is carried by the box's <c>PlaceholderText</c> instead, so the reader
    /// still sees what the target is. Choosing it closes the editor and writes nothing, which
    /// <see cref="CommitObjectType"/> enforces.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanEditObjectType))]
    private void BeginEditObjectType()
    {
        // TRACKING item 13: RelayCommand.Execute ignores CanExecute, so the guard is here as well
        // as on the command. An obj: group has no targets row and must not open an editor whose
        // commit could never write.
        if (!CanEditObjectType)
        {
            return;
        }

        SelectedObjectTypeChoice = null;
        IsEditingObjectType = true;
    }

    /// <summary>Spec 12.4's Escape: closes the box and leaves the stored value alone.</summary>
    [RelayCommand]
    private void CancelObjectType()
    {
        IsEditingObjectType = false;
        SelectedObjectTypeChoice = null;
    }

    /// <summary>
    /// Writes the chosen category through the write delegate and shows the new value at once.
    /// </summary>
    /// <remarks>
    /// The displayed value is updated here rather than on the next load because the page reloads
    /// on a scan and not on a write, and a value that reverts on screen for two seconds reads as a
    /// failed write. The code shown is the one the repository stored, read back from the one table
    /// that holds spec 9.8's reverse direction rather than from a second copy of it.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCommitObjectType))]
    private void CommitObjectType()
    {
        // The same guard as CanExecute (TRACKING item 13), plus the target id it implies.
        if (!CanCommitObjectType()
            || Block.TargetId is not { } targetId
            || SelectedObjectTypeChoice is not { } category)
        {
            return;
        }

        // The entry the target already has. It is a real selection now that the box opens
        // unseeded, so it closes the editor, and it is not a write: the stored code would not
        // change and an activity event saying a type changed to itself would be false.
        if (string.Equals(category, ObjectCategory, StringComparison.Ordinal))
        {
            IsEditingObjectType = false;
            return;
        }

        // Phase review P3-5: the display follows the write and never leads it. With no write
        // delegate nothing reaches the database, so nothing may reach the screen either; the row
        // keeps the stored value and the editor closes, because the edit is over. Latent today,
        // because AppHost always wires the delegate.
        if (_setObjectType is not { } write)
        {
            IsEditingObjectType = false;
            return;
        }

        write(targetId, category);
        ObjectType = TargetWriteRepository.FirstSimbadCode(category);
        ObjectCategory = category;
        IsEditingObjectType = false;
    }

    private bool CanCommitObjectType()
        => CanEditObjectType && !string.IsNullOrEmpty(SelectedObjectTypeChoice);

    public string Constellation => Block.Constellation ?? "";

    public bool HasConstellation => !string.IsNullOrWhiteSpace(Block.Constellation);

    /// <summary>Right ascension as <c>HH:mm:ss.s</c>, converted from degrees.</summary>
    public string RaText { get; }

    public bool HasRa => RaText.Length > 0;

    /// <summary>Declination as <c>+DD:MM:SS</c>.</summary>
    public string DecText { get; }

    public bool HasDec => DecText.Length > 0;

    /// <summary>Apparent size in arcminutes, for example <c>12.3' x 4.5'</c>. The major axis
    /// alone when the minor axis is absent, empty when both are.</summary>
    public string SizeText { get; }

    public bool HasSize => SizeText.Length > 0;

    /// <summary>Position angle in degrees, for example <c>145 deg</c>. Spelled out rather than
    /// carrying the degree sign, which the house style forbids in a format string.</summary>
    public string PositionAngleText { get; }

    public bool HasPositionAngle => PositionAngleText.Length > 0;

    /// <summary>V magnitude, two decimals. The unit lives in the row label.</summary>
    public string VMagText { get; }

    public bool HasVMag => VMagText.Length > 0;

    /// <summary>Surface brightness, one decimal. The unit lives in the row label.</summary>
    public string SurfaceBrightnessText { get; }

    public bool HasSurfaceBrightness => SurfaceBrightnessText.Length > 0;

    public string SacDescription => Block.SacDescription ?? "";

    public bool HasSacDescription => !string.IsNullOrWhiteSpace(Block.SacDescription);

    public string SacNotes => Block.SacNotes ?? "";

    public bool HasSacNotes => !string.IsNullOrWhiteSpace(Block.SacNotes);

    /// <summary>Spec 12.4's membership badges (spec 5.4, 9.8), already ordered by catalog name.
    /// </summary>
    public IReadOnlyList<CatalogMembershipBadge> CatalogMemberships { get; }

    public bool HasCatalogMemberships => CatalogMemberships.Count > 0;

    /// <summary>Drives the reference thumbnail slot. Spec 11.4's pass renders the image on the
    /// scan thread; this page only knows whether there is one.</summary>
    public bool HasReferenceThumbnail => !string.IsNullOrWhiteSpace(Block.ReferenceThumbnailPath);

    /// <summary>Spec 12.4's reference thumbnail, as the same placeholder-to-image slot the
    /// preview modal uses. Null when the target has no stored path, or when no factory was
    /// supplied (a unit test; the CLI has no pages at all): the view then shows spec 12.10's empty
    /// state.</summary>
    public ThumbnailSlotViewModel? ReferenceThumbnail { get; }

    /// <summary>Spec 5.3's <c>user_defined</c> flag, shown as a badge beside the name.</summary>
    public bool IsUserDefined => Block.UserDefined;

    // ---- P12: the arcminute scale bar over the reference thumbnail ---------------------------

    /// <summary>
    /// The comp's arcminute scale bar label, for example <c>15'</c>. The bar's angular length is
    /// the largest of 1, 2, 5, 10, 15, 30, 60 and 120 arcminutes that is at most a third of the
    /// reference frame's field of view, so the bar never spans the thumbnail and never disappears.
    /// Empty, with <see cref="HasScaleBar"/> false, when the block carries no plate scale or no
    /// frame width.
    /// </summary>
    /// <remarks>
    /// <see cref="TargetHeaderBlock.SizeMajor"/> is deliberately not an input: the bar measures
    /// the frame's field, not the object, which is what a scale bar over an image means and what
    /// ruling Q6 settled. The task table's phrase "from angular size and the reference frame's
    /// plate scale" reads the other way and was withdrawn by that ruling. <see cref="SizeText"/>
    /// still renders the object's own angular size in the Details drawer and is unchanged.
    /// </remarks>
    public string ScaleBarText { get; }

    /// <summary>The bar's length in device-independent pixels across the thumbnail box, which is
    /// <see cref="DetailThumbnailSize"/> wide. Zero when there is no bar.</summary>
    public double ScaleBarWidth { get; }

    public bool HasScaleBar => ScaleBarWidth > 0;

    /// <summary>The reference thumbnail's edge on the redesigned page, in device-independent
    /// pixels. The view declares the same figure as its own <c>DetailThumbnailSize</c> resource;
    /// the bar's length is a fraction of it, so the arithmetic needs it here too.</summary>
    internal const double DetailThumbnailSize = 128d;

    // The comp's ladder. Ascending, so the last candidate that fits is the largest one.
    private static readonly double[] ScaleBarStepsArcmin = [1d, 2d, 5d, 10d, 15d, 30d, 60d, 120d];

    internal static (double Width, string Text) ScaleBar(double? arcsecPerPixel, int? frameWidthPixels)
    {
        if (arcsecPerPixel is not { } scale
            || !double.IsFinite(scale)
            || scale <= 0d
            || frameWidthPixels is not { } pixels
            || pixels <= 0)
        {
            return (0d, "");
        }

        var fieldArcmin = pixels * scale / 60d;
        if (!double.IsFinite(fieldArcmin) || fieldArcmin <= 0d)
        {
            return (0d, "");
        }

        // At most a third of the field: a bar longer than that reads as a measurement of the
        // frame rather than as a ruler laid on it.
        var limit = fieldArcmin / 3d;
        double? step = null;
        foreach (var candidate in ScaleBarStepsArcmin)
        {
            if (candidate <= limit)
            {
                step = candidate;
            }
        }

        // A field under three arcminutes has no step on the ladder, and inventing one below the
        // smallest would give a bar of a few pixels. No bar at all is the honest answer.
        return step is { } chosen
            ? (chosen / fieldArcmin * DetailThumbnailSize, MetricText.Format(chosen, "0", "'"))
            : (0d, "");
    }

    // Right ascension is stored in degrees and shown in hours: 15 degrees is one hour. The carry
    // is done on the rounded total rather than per component, so 23:59:59.96 renders as 00:00:00.0
    // instead of 23:59:60.0.
    internal static string FormatRightAscension(double? degrees)
    {
        if (degrees is not { } value || !double.IsFinite(value))
        {
            return "";
        }

        var seconds = Math.Round(Wrap(value, 360d) / 15d * 3600d, 1, MidpointRounding.AwayFromZero);
        var hours = (int)Math.Floor(seconds / 3600d) % 24;
        var minutes = (int)Math.Floor(seconds % 3600d / 60d);
        var wholeSeconds = seconds % 60d;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{wholeSeconds:00.0}");
    }

    internal static string FormatDeclination(double? degrees)
    {
        if (degrees is not { } value || !double.IsFinite(value))
        {
            return "";
        }

        var sign = value < 0 ? "-" : "+";
        var seconds = Math.Round(Math.Abs(value) * 3600d, 0, MidpointRounding.AwayFromZero);
        var wholeDegrees = (int)Math.Floor(seconds / 3600d);
        var minutes = (int)Math.Floor(seconds % 3600d / 60d);
        var wholeSeconds = (int)(seconds % 60d);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{wholeDegrees:00}:{minutes:00}:{wholeSeconds:00}");
    }

    internal static string FormatSize(double? major, double? minor)
    {
        if (major is null && minor is null)
        {
            return "";
        }

        // A minor axis with no major axis is not a shape the catalogs produce, but rendering the
        // one value that is present beats rendering nothing.
        var first = major ?? minor;
        if (major is null || minor is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{first:0.0}'");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{major:0.0}' x {minor:0.0}'");
    }

    // Keeps a hand-edited or catalog-supplied right ascension inside one turn before the
    // sexagesimal conversion, so a nonsense 720 does not render as hour 48.
    private static double Wrap(double value, double period)
    {
        var wrapped = value % period;
        return wrapped < 0 ? wrapped + period : wrapped;
    }
}
