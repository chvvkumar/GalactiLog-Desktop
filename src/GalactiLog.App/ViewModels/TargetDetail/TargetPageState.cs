using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// The one live copy of spec 5.8.2's <c>display.target_page</c> choices: the grading baseline and
/// the Copy Frame List dialog's three and the lanes heights. One instance per process, registered in <c>AppHost</c>.
/// </summary>
/// <remarks>
/// <para>
/// The choices persist per profile, which is one value per key. Seeding
/// each card and each page from the startup document instead gave the process one copy per
/// object, each frozen at the value the process started with: a toggle on one night was invisible
/// on the next night, and a page opened after a toggle was rebuilt from the snapshot and showed
/// the old value until a relaunch. The holder is what makes the document's
/// single value a single value in the running process too.
/// </para>
/// <para>
/// The shape is <see cref="ChartSelectionViewModel"/>'s, which holds spec 5.8.3's chart
/// disclosure the same way: a singleton seeded once from the document, written through its
/// writer on change, and read live by every view-model that shows the flag.
/// </para>
/// </remarks>
public sealed partial class TargetPageState : ObservableObject
{
    // The one write of display.target_page, normally DisplayColumnWriter.Write. Null in a test
    // that does not assert persistence, which makes every toggle a pure in-memory flip.
    private readonly Action<Func<DisplaySettings, DisplaySettings>>? _writeDisplay;

    /// <param name="stored">The stored record, normally <c>display.target_page</c> read once on
    /// the host's thread. Null is a fresh profile.</param>
    /// <param name="writeDisplay">Normally <c>DisplayColumnWriter.Write</c>, the one chain over
    /// the display document. Null leaves every toggle unpersisted.</param>
    public TargetPageState(
        TargetPageSettings? stored = null,
        Action<Func<DisplaySettings, DisplaySettings>>? writeDisplay = null)
    {
        var seed = stored ?? new TargetPageSettings();
        _writeDisplay = writeDisplay;

        // Backing fields, not the properties: setting the properties here would queue a write of
        // the values just read back out of the document (ChartSelectionViewModel's constructor
        // follows the same rule for spec 5.8.3's two chart flags).
        _gradingBaseline = ParseBaseline(seed.GradingBaseline);
        _frameListFormat = seed.FrameListFormat;
        _frameListMode = seed.FrameListMode;
        _frameListIncludeUnmeasured = seed.FrameListIncludeUnmeasured;
        foreach (var (key, state) in seed.Layouts)
        {
            _layouts[key] = state ?? new TargetLayoutState();
        }
    }

    private readonly Dictionary<string, TargetLayoutState> _layouts = new();

    /// <summary>A layout's stored state; a fresh record for a key never written.</summary>
    public TargetLayoutState Layout(string layoutKey) => _layouts.GetValueOrDefault(layoutKey) ?? new TargetLayoutState();

    /// <summary>Stores one change to a layout's state. One queued write per change; a change that
    /// leaves the record equal writes nothing. The change is applied again inside the queued write,
    /// to the layout entry as loaded, so it clobbers no sibling key.</summary>
    public void SetLayout(string layoutKey, Func<TargetLayoutState, TargetLayoutState> change)
    {
        var next = change(Layout(layoutKey));
        if (next == Layout(layoutKey))
        {
            return;
        }

        _layouts[layoutKey] = next;
        _writeDisplay?.Invoke(document => document with
        {
            TargetPage = document.TargetPage with
            {
                Layouts = new Dictionary<string, TargetLayoutState>(document.TargetPage.Layouts)
                {
                    [layoutKey] = change(document.TargetPage.Layouts.GetValueOrDefault(layoutKey) ?? new TargetLayoutState()),
                },
            },
        });
    }

    /// <summary>The lanes region's stored height for a layout key; null is the layout's automatic rule.</summary>
    public double? LanesHeight(string layoutKey) => Layout(layoutKey).LanesHeight;

    /// <summary>Stores a layout's lanes height, or null to return to the automatic rule.</summary>
    public void SetLanesHeight(string layoutKey, double? value) => SetLayout(layoutKey, state => state with { LanesHeight = value });

    /// <summary>Spec 5.8.2's "a stored value outside a key's listed set reads as that key's
    /// default rather than throwing". Only <c>rig</c>, ordinal and case insensitive, is
    /// <see cref="GradingBaseline.Rig"/>; everything else, junk included, is the default.</summary>
    public static GradingBaseline ParseBaseline(string? stored)
        => string.Equals(stored, "rig", StringComparison.OrdinalIgnoreCase)
            ? GradingBaseline.Rig
            : GradingBaseline.Session;

    /// <summary>The stored form of <paramref name="baseline"/>, the lower-case literal spec
    /// 5.8.2's table lists.</summary>
    public static string BaselineToStored(GradingBaseline baseline)
        => baseline == GradingBaseline.Rig ? "rig" : "session";

    /// <summary>Spec 12.4's "Compare to" baseline, <c>grading_baseline</c>. The session's own
    /// frames on a fresh profile. Page wide, so switching nights keeps it.</summary>
    [ObservableProperty]
    private GradingBaseline _gradingBaseline;

    /// <summary>Spec 12.4's Copy Frame List format, <c>frame_list_format</c>. The stored literal,
    /// <c>paths</c> on a fresh profile.</summary>
    [ObservableProperty]
    private string _frameListFormat = "paths";

    /// <summary>Spec 12.4's Copy Frame List mode, <c>frame_list_mode</c>. The stored literal,
    /// <c>good</c> on a fresh profile.</summary>
    [ObservableProperty]
    private string _frameListMode = "good";

    /// <summary>Spec 12.4's Copy Frame List unmeasured box,
    /// <c>frame_list_include_unmeasured</c>. Checked on a fresh profile.</summary>
    [ObservableProperty]
    private bool _frameListIncludeUnmeasured = true;

    // The persistence for the section keys. Each mutation is a nested with expression applied to
    // the document as loaded inside the queued write, never to a snapshot taken here, which is
    // what keeps one of these toggles from clobbering a column write or either of the other two
    // over the same target_page object.
    partial void OnGradingBaselineChanged(GradingBaseline value)
        => _writeDisplay?.Invoke(document => document with
        {
            TargetPage = document.TargetPage with { GradingBaseline = BaselineToStored(value) },
        });

    partial void OnFrameListFormatChanged(string value)
        => _writeDisplay?.Invoke(document => document with
        {
            TargetPage = document.TargetPage with { FrameListFormat = value },
        });

    partial void OnFrameListModeChanged(string value)
        => _writeDisplay?.Invoke(document => document with
        {
            TargetPage = document.TargetPage with { FrameListMode = value },
        });

    partial void OnFrameListIncludeUnmeasuredChanged(bool value)
        => _writeDisplay?.Invoke(document => document with
        {
            TargetPage = document.TargetPage with { FrameListIncludeUnmeasured = value },
        });
}
