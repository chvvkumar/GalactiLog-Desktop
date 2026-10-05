using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.Settings;
using Serilog;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Design-spec 12.7's Library tab. The only logic here is the folder picker: HANDOFF.md section
/// 5 keeps pickers as Avalonia storage-provider calls in the view rather than a new service, so
/// the view-model stays constructible with no window.
/// </summary>
/// <remarks>
/// The picker returns a path and nothing else happens to it here: the tab neither enumerates,
/// creates nor deletes anything under it (design-spec 2.1). A folder chosen while the picker is
/// unavailable (a headless test, a top level that is not a window) simply adds nothing.
/// </remarks>
public partial class LibraryTabView : UserControl
{
    public LibraryTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Spec 12.7: the scan filter notice's Review action "scrolls to this tab's rule editor rather
    /// than navigating". Avalonia's BringIntoView is the mechanism, and it lives here for the same
    /// reason the folder pickers do: it is a view gesture over a control, not view-model state.
    /// </summary>
    private void OnReviewScanFilters(object? sender, RoutedEventArgs e) => ScrollNameRulesIntoView();

    private void ScrollNameRulesIntoView() => this.FindControl<Border>("NameRulesSection")?.BringIntoView();

    /// <summary>
    /// Phase 15B Task 5c, spec 12.5: the Statistics page's Guiding empty notice offers "Enable
    /// guide log scanning", and the shell's one Settings route asks this tab for the switch.
    /// </summary>
    /// <remarks>
    /// The switch itself rather than the <c>ScanOptionsSection</c> border that holds it: the
    /// section is the tallest on the tab and the switch is its last control, so scrolling the
    /// section's own rect into view leaves the switch below the fold, which is the defect
    /// <see cref="ScrollNameRulesIntoView"/> exists to avoid one section further up. The element
    /// was already named by Phase 15A, so nothing in the markup moved for this.
    /// </remarks>
    private void ScrollGuideLogSwitchIntoView()
        => this.FindControl<CheckBox>("Phd2ScanEnabledCheckBox")?.BringIntoView();

    /// <summary>
    /// Spec 12.7's "cleared on every visit to the tab rather than remembered". The tab view-model
    /// is a lazily constructed singleton that outlives one visit, so clearing the box in its
    /// constructor would remember it for the life of the process; the view is attached again on
    /// every visit, which is the visit signal.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not LibraryTabViewModel viewModel)
        {
            return;
        }

        viewModel.ClearPerRunOptions();

        // Phase 14B fixer, fixer list item 4. Spec 12.2's Dashboard Review route selects this tab
        // from the shell and asks for the rule editor to be in view; the request lands on the
        // view-model because the shell runs before this view is attached, and often before the
        // tab is constructed at all.
        if (viewModel.ConsumeNameRulesInViewRequest())
        {
            ScrollWhenReady(viewModel, ScrollNameRulesIntoView);
        }

        // The same route for Phase 15B Task 5c's guide log switch.
        if (viewModel.ConsumeGuideLogSwitchInViewRequest())
        {
            ScrollWhenReady(viewModel, ScrollGuideLogSwitchIntoView);
        }
    }

    // The pending wait, so a detach drops a handler this view put on a tab view-model that
    // outlives it. Null when nothing is waiting.
    private Action? _cancelPendingScroll;

    /// <summary>
    /// Runs a scroll once this tab has something to scroll to: <see cref="SettingsTabScroll"/>
    /// with this tab's gate, which is <c>IsReady</c> because every section here carries
    /// <c>IsVisible="{Binding IsReady}"</c> and a collapsed target has no rect to scroll to.
    /// </summary>
    /// <remarks>
    /// Review P3-5: one wait at a time, and the later request wins. Both sections live on this one
    /// tab, so two pending requests would mean scrolling to two places on one visit; the request
    /// the user made last is the one they are owed, and it is the one the second call arms. A
    /// visit can only carry two if two routes fired between visits, which no surface does today.
    /// </remarks>
    private void ScrollWhenReady(LibraryTabViewModel viewModel, Action scroll)
    {
        _cancelPendingScroll?.Invoke();
        _cancelPendingScroll = SettingsTabScroll.RunWhenReady(
            viewModel, () => viewModel.IsReady, nameof(LibraryTabViewModel.IsReady), scroll);
    }

    /// <summary>Drops a wait that never came good, so a failed read leaves no handler on the tab.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cancelPendingScroll?.Invoke();
        _cancelPendingScroll = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnBrowseScanRoot(object? sender, RoutedEventArgs e)
        => await BrowseAsync("Choose a library folder", (viewModel, path) => viewModel.AddScanRoot(path));

    private async void OnBrowseIncludePath(object? sender, RoutedEventArgs e)
        => await BrowseAsync("Choose a folder to include", (viewModel, path) => viewModel.AddIncludePath(path));

    private async void OnBrowseExcludePath(object? sender, RoutedEventArgs e)
        => await BrowseAsync("Choose a folder to exclude", (viewModel, path) => viewModel.AddExcludePath(path));

    private async Task BrowseAsync(string title, Action<LibraryTabViewModel, string> add)
    {
        if (DataContext is not LibraryTabViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            // AllowMultiple, matching the web's multi-select folder picker: adding three roots or
            // three exclude paths in one gesture is the common case.
            folders = await storage
                .OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = true })
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // This is an async void handler's continuation: an exception escaping here has
            // nowhere to go but the dispatcher's unhandled path. A picker that could not open
            // adds nothing, which is what a cancelled picker does too.
            //
            // Review finding M7: logged, so a storage-provider fault is distinguishable in the
            // log from a cancelled picker. The static Serilog logger rather than an injected one
            // because a view is constructed by the XAML loader with no constructor arguments, and
            // a logging delegate on the view-model would put a view concern on the view-model.
            Log.Warning(ex, "The folder picker for {PickerTitle} could not be opened", title);
            return;
        }

        foreach (var folder in folders)
        {
            // TryGetLocalPath is null for a non-filesystem location (a cloud provider shell
            // folder); there is nothing this application could scan there, so it is skipped.
            if (folder.TryGetLocalPath() is { Length: > 0 } path)
            {
                add(viewModel, path);
            }
        }
    }
}
