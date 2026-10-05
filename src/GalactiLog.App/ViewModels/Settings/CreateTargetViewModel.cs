using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's Create target form (PAR-001), opened from the Targets tab or from a row of the
/// unresolved-name list. Six fields, a "user defined" checkbox, and the two refusals spec 9.7
/// returns as outcomes.
/// </summary>
/// <remarks>
/// <para>
/// Every collaborator arrives as a delegate, the rule this tab already follows (design-spec 18.3),
/// so the form builds in a unit test with no database. The write itself is
/// <c>TargetWriteRepository.CreateUserDefined</c>, which runs off the UI thread through
/// <see cref="Task.Run(Action)"/> exactly as this tab's other commands do.
/// </para>
/// <para>
/// One form, one path: the unresolved entry point (<see cref="OpenForCommand"/>) differs only in
/// what it puts in the fields. Spec 9.7's retro-link is the repository's and runs for both.
/// </para>
/// </remarks>
public sealed partial class CreateTargetViewModel : ObservableObject
{
    private readonly Func<CreateTargetRequest, CreateTargetResult> _create;
    private readonly Action? _afterCreate;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    /// <param name="create">Normally <c>TargetWriteRepository.CreateUserDefined</c>.</param>
    /// <param name="afterCreate">How the tab reloads its candidate list and the two lists below it
    /// after a create, which is the same reload an accepted merge runs. Null skips it, which is
    /// what a test that is not about the reload wants.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed write is logged, never rethrown on the UI thread.
    /// </param>
    public CreateTargetViewModel(
        Func<CreateTargetRequest, CreateTargetResult> create,
        Action? afterCreate = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _create = create;
        _afterCreate = afterCreate;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        PrimaryName = "";
        OtherObjectType = "";
        Ra = "";
        Dec = "";
        CatalogId = "";
        AliasText = "";

        // Departure 5 (task1-report.md): the checkbox ships checked. The web application defaults
        // it off and sets it only for a solar system type; here a target the catalogues do not
        // carry is the whole reason this form exists, and it stays the user's to clear.
        UserDefined = true;
    }

    /// <summary>
    /// The empty choice, then spec 9.8's nine display categories, its five solar system
    /// categories, then <c>Other</c>.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="ObjectTypeCategories"/> rather than retyped, the way
    /// <c>FilterPanelViewModel</c> concatenates the same two arrays for the dashboard pills: a
    /// second copy of fifteen strings is exactly the drift a census test would have to chase. The
    /// empty entry is a choice in the list rather than a placeholder, so a user who picked a type
    /// can put it back to none.
    /// <para>
    /// This list is deliberately not <c>TargetHeaderViewModel.Choices</c>: 14A's object type
    /// editor on Target detail withholds the solar system categories, because it edits a target
    /// the catalogues already describe (task1-report.md departure 4).
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> AllObjectTypeChoices =
    [
        "",
        .. ObjectTypeCategories.DisplayCategories,
        .. ObjectTypeCategories.SolarSystemCategories,
        TargetWriteRepository.OtherCategory,
    ];

    /// <inheritdoc cref="AllObjectTypeChoices"/>
    public IReadOnlyList<string> ObjectTypeChoices => AllObjectTypeChoices;

    /// <summary>True while the form is shown. The button that opens it is always visible; the
    /// fields are not.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string PrimaryName { get; set; }

    /// <summary>The chosen entry of <see cref="ObjectTypeChoices"/>. The empty string leaves
    /// <c>object_type</c> null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOtherBox))]
    [NotifyPropertyChangedFor(nameof(CoordinatesEnabled))]
    public partial string? SelectedObjectType { get; set; }

    /// <summary>The free text typed under <c>Other</c>.</summary>
    [ObservableProperty]
    public partial string OtherObjectType { get; set; }

    /// <summary>Spec 12.7: <c>Other</c> reveals a free-text box.</summary>
    public bool ShowOtherBox
        => string.Equals(SelectedObjectType, TargetWriteRepository.OtherCategory, StringComparison.Ordinal);

    /// <summary>
    /// False while a solar system category is chosen, because spec 12.7 clears RA and Dec for one
    /// and "a fixed position is meaningless for a moving object".
    /// </summary>
    /// <remarks>Disabled rather than merely emptied: the rule is that the boxes stay empty while
    /// that category is selected, and a disabled box is the one shape that cannot be typed back
    /// into without changing the category first.</remarks>
    public bool CoordinatesEnabled => !IsSolarSystem(SelectedObjectType);

    /// <summary>Degrees, as typed. Blank is a valid value and stores null.</summary>
    [ObservableProperty]
    public partial string Ra { get; set; }

    /// <inheritdoc cref="Ra"/>
    [ObservableProperty]
    public partial string Dec { get; set; }

    [ObservableProperty]
    public partial string CatalogId { get; set; }

    /// <summary>The comma-separated alias list, as typed.</summary>
    [ObservableProperty]
    public partial string AliasText { get; set; }

    [ObservableProperty]
    public partial bool UserDefined { get; set; }

    /// <summary>The last refusal, or the sentence a successful create reported. Null until
    /// something happens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>True while the write is in flight, so the submit button greys.</summary>
    [ObservableProperty]
    public partial bool IsSubmitting { get; private set; }

    /// <summary>The in-flight submit, so a test can await it instead of sleeping. Mirrors
    /// <c>TargetsTabViewModel.PendingLoad</c>.</summary>
    internal Task? PendingSubmit { get; private set; }

    /// <summary>Spec 12.7's refusal for an empty primary name, verbatim.</summary>
    internal const string PrimaryNameRequiredMessage = "Primary name is required";

    /// <summary>Opens an empty form, or leaves an open one exactly as it is.</summary>
    /// <remarks>
    /// Phase 14B fixer, fixer list item 11 (phase review P3-4). This used to call
    /// <see cref="Reset"/> unconditionally, and the tab header's "New target" button carries no
    /// term against <see cref="IsOpen"/>, so a stray press while the form held seven typed fields
    /// discarded them with no warning. Ruling D4's one shared instance is what makes that
    /// destructible, so the guard belongs here rather than on one button. The form is already
    /// empty on the first press, so an early return costs the caller nothing.
    /// </remarks>
    [RelayCommand]
    private void Open()
    {
        if (IsOpen)
        {
            return;
        }

        Reset();
        Message = null;
        IsOpen = true;
    }

    /// <summary>
    /// Spec 12.7's unresolved entry point: the same form with the primary name pre-filled from
    /// that <c>OBJECT</c> string and that string already in the alias list.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Open"/> this one re-seeds an already-open form (fixer list item 11): it
    /// carries a name the reader chose on a specific unresolved row, and leaving the form showing
    /// a different name than the row they pressed would be the worse of the two surprises. The
    /// draft is replaced, deliberately and with the new name visible in it.
    /// </remarks>
    [RelayCommand]
    public void OpenFor(string name)
    {
        Reset();
        Message = null;
        PrimaryName = name;
        AliasText = name;
        IsOpen = true;
    }

    /// <summary>Closes the form and clears what was typed.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Reset();
        Message = null;
        IsOpen = false;
    }

    /// <summary>
    /// Spec 9.7's create: validates, builds the request, writes off the UI thread and reports what
    /// happened.
    /// </summary>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (FIXER F24, deviation D10): a command built
    /// from a <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight token on a second
    /// <c>Execute</c>, which would abort a write already in progress rather than be refused by the
    /// guard below.
    /// <para>
    /// Every refusal is re-checked in the body rather than only in <c>CanExecute</c>, because
    /// <c>RelayCommand.Execute</c> ignores <c>CanExecute</c> (TRACKING section 6 item 13).
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        var primaryName = PrimaryName.Trim();
        if (primaryName.Length == 0)
        {
            Message = PrimaryNameRequiredMessage;
            return;
        }

        if (!TryParseCoordinate(Ra, 0d, 360d, out var ra))
        {
            Message = RaRangeMessage;
            return;
        }

        if (!TryParseCoordinate(Dec, -90d, 90d, out var dec))
        {
            Message = DecRangeMessage;
            return;
        }

        var request = new CreateTargetRequest(
            primaryName,
            ChosenObjectType(),
            ra,
            dec,
            string.IsNullOrWhiteSpace(CatalogId) ? null : CatalogId.Trim(),
            SplitAliases(AliasText),
            UserDefined);

        Message = null;
        IsSubmitting = true;

        try
        {
            var run = Task.Run(() => _create(request));
            PendingSubmit = run;
            var result = await run.ConfigureAwait(false);

            _post(() => Publish(primaryName, result));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Creating the target {PrimaryName} failed", primaryName);
            _post(() =>
            {
                IsSubmitting = false;
                Message = "The target could not be created. See the log for details.";
            });
        }
    }

    // Runs on the UI thread through the post seam. A refusal leaves every field holding what the
    // user typed (spec 9.7); only a create clears the form.
    private void Publish(string primaryName, CreateTargetResult result)
    {
        IsSubmitting = false;

        switch (result.Outcome)
        {
            case CreateTargetOutcome.NameInUse:
                Message = $"Name already in use by \"{result.ConflictingTargetName}\". "
                    + "Use the merge action to attach these frames to it.";
                return;

            case CreateTargetOutcome.CatalogIdInUse:
                Message = $"Catalog ID already belongs to \"{result.ConflictingTargetName}\".";
                return;

            // Phase 14B fixer, fixer list item 22 (task3-review P3). The target and its frame
            // links are committed; only the activity row and the pending merge candidates are
            // not. Reporting the old "could not be created" sentence for this was false, and the
            // reader's re-submit was then refused by the name conflict against the row that had in
            // fact been created. The form closes, because the create happened.
            case CreateTargetOutcome.CreatedWithoutBookkeeping:
                Message =
                    $"Created {primaryName}, linked {UnresolvedNamesViewModel.Plural(result.LinkedFrames, "frame")} "
                    + $"from {UnresolvedNamesViewModel.Plural(result.ClosedCandidates, "unresolved name")}. "
                    + "The activity entry could not be written. See the log for details.";
                Reset();
                IsOpen = false;
                _afterCreate?.Invoke();
                return;

            default:
                // Spec 12.7's sentence, with the plurals UnresolvedNamesViewModel.Plural gives.
                Message =
                    $"Created {primaryName}, linked {UnresolvedNamesViewModel.Plural(result.LinkedFrames, "frame")} "
                    + $"from {UnresolvedNamesViewModel.Plural(result.ClosedCandidates, "unresolved name")}";
                Reset();
                IsOpen = false;
                _afterCreate?.Invoke();
                return;
        }
    }

    /// <summary>Spec 12.7's RA bound, with the range in the message.</summary>
    internal const string RaRangeMessage = "RA must be a number between 0 and 360.";

    /// <summary>Spec 12.7's Dec bound.</summary>
    internal const string DecRangeMessage = "Dec must be a number between -90 and 90.";

    /// <summary>
    /// Spec 12.7: a blank parses to null, and the bounds are inclusive at both ends.
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.InvariantCulture"/>, never the current culture: <c>ra</c> and
    /// <c>dec</c> are <c>REAL</c> columns and "10,5" on a German profile would parse to 105 under
    /// the current culture, which is a different position in the sky rather than a formatting
    /// difference. The current culture was considered and rejected for that reason.
    /// </remarks>
    private static bool TryParseCoordinate(string text, double low, double high, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed < low
            || parsed > high)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    // Spec 12.7 step 4: the chosen display category literal, the free text typed under Other, or
    // null when nothing was chosen.
    private string? ChosenObjectType()
    {
        if (string.IsNullOrWhiteSpace(SelectedObjectType))
        {
            return null;
        }

        if (!ShowOtherBox)
        {
            return SelectedObjectType;
        }

        return string.IsNullOrWhiteSpace(OtherObjectType) ? null : OtherObjectType.Trim();
    }

    private static List<string> SplitAliases(string text)
        => [.. text.Split(',').Select(alias => alias.Trim()).Where(alias => alias.Length > 0)];

    private static bool IsSolarSystem(string? category)
        => category is not null
            && ObjectTypeCategories.SolarSystemCategories.Contains(category, StringComparer.Ordinal);

    // Spec 12.7: a solar system category clears RA and Dec, because a fixed position is
    // meaningless for a moving object, and sets the "user defined" checkbox.
    partial void OnSelectedObjectTypeChanged(string? value)
    {
        if (!IsSolarSystem(value))
        {
            return;
        }

        Ra = "";
        Dec = "";
        UserDefined = true;
    }

    private void Reset()
    {
        PrimaryName = "";
        SelectedObjectType = null;
        OtherObjectType = "";
        Ra = "";
        Dec = "";
        CatalogId = "";
        AliasText = "";
        UserDefined = true;
    }
}
