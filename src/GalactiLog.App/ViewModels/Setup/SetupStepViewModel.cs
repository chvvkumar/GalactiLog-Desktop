using GalactiLog.App.ViewModels.Wizard;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// One step of design-spec 12.1's setup wizard. The wizard owns the order, the header, the footer
/// and the persistence; a step owns its own fields, its own inline validation and the single
/// mutation that writes those fields into the <c>general</c> document.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Apply"/> is a pure function from the stored document to the document that should be
/// written. It runs inside <c>SettingsStore.MutateGeneral</c>'s critical section, on a background
/// thread, so it must do no I/O, take no lock, touch no observable collection and never call back
/// into the store. Everything it needs is captured before the call.
/// </para>
/// <para>
/// A step never writes anything itself. The wizard persists it (spec 12.1: "Each step persists its
/// own settings before advancing, so a failure keeps the user on the step rather than losing
/// input"), which is what keeps the failure path in one place instead of five.
/// </para>
/// </remarks>
public abstract partial class SetupStepViewModel : WizardStepViewModel
{
    protected SetupStepViewModel(Action<Action>? post = null, ILogger? logger = null)
        : base(post, logger)
    {
    }

    /// <summary>
    /// Spec 12.12's help topic for this step, derived from the step rather than stored, so the
    /// wizard's one heading row carries one markup site for all five steps. The same shape ruling
    /// Q2 gave the seven Diagnostics groups.
    /// </summary>
    public override string HelpTopicId => TopicIdFor(GetType());

    /// <summary>
    /// The five step types in spec 12.1's wizard order. It is the source
    /// <see cref="HelpTopicIds"/> is built from, so the id set cannot drift from the switch.
    /// </summary>
    private static readonly IReadOnlyList<Type> StepTypes =
    [
        typeof(ScanFoldersStepViewModel),
        typeof(ThumbnailCacheStepViewModel),
        typeof(ObserverLocationStepViewModel),
        typeof(ScanOptionsStepViewModel),
        typeof(FirstScanStepViewModel),
    ];

    /// <summary>
    /// Every topic id <see cref="TopicIdFor"/> can produce, in wizard order. The help placement
    /// census reads this rather than restating the five ids, because two copies of a list drift.
    /// </summary>
    public static IReadOnlyList<string> HelpTopicIds { get; } = [.. StepTypes.Select(TopicIdFor)];

    /// <summary>
    /// The help topic id for a step. Total on purpose and throwing on an unknown step: a sixth
    /// wizard step is then a compile-time decision rather than a silently missing glyph.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The type is not one of spec 12.1's five
    /// steps.</exception>
    public static string TopicIdFor(Type stepType) => stepType.Name switch
    {
        nameof(ScanFoldersStepViewModel) => "setup.scan-folders",
        nameof(ThumbnailCacheStepViewModel) => "setup.storage",
        nameof(ObserverLocationStepViewModel) => "setup.observer",
        nameof(ScanOptionsStepViewModel) => "setup.scan-options",
        nameof(FirstScanStepViewModel) => "setup.first-scan",
        _ => throw new ArgumentOutOfRangeException(
            nameof(stepType),
            stepType.Name,
            "A wizard step carries a help topic. Add its id to HelpTopics and to this switch, and "
            + "the placement census will then require it to be placed."),
    };

    /// <summary>Seeds the step's controls from the stored document. Runs on the UI thread, once,
    /// before the wizard is shown.</summary>
    public abstract void Load(GeneralSettings general);

    /// <summary>
    /// This step's fields, written into the document. Pure and fast: it runs inside the store's
    /// write gate.
    /// </summary>
    public abstract GeneralSettings Apply(GeneralSettings general);
}
