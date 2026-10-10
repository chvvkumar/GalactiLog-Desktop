using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using Serilog;

namespace GalactiLog.App.Views.TargetDetail.Wbpp;

/// <summary>
/// Spec 12.13's Export for stacking wizard. The subscription, the
/// <see cref="ViewModels.IModalPageViewModel.CloseRequested"/> to <c>Close(result)</c> path and the
/// veto gate belong to <see cref="ModalPageWindow{TViewModel}"/>; what this window owns is
/// <see cref="RefuseClose"/>, Escape and the two platform dialog seams.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seams are bound in <c>OnOpened</c> and cleared in <c>OnClosed</c>.</b>
/// <see cref="ModalPageWindow{TViewModel}"/> seals <c>OnDataContextChanged</c> and
/// <c>OnClosing</c>, so the log viewer's own hook is not available here; the clear carries the
/// same "only when it is still mine" guard <c>LogViewerView.axaml.cs</c> and
/// <c>DiagnosticsView.axaml.cs</c> both use, so two windows cannot leave a dangling picker.
/// </para>
/// <para>
/// Neither picker is reachable headless: the headless top level offers no storage provider at all,
/// so both return null before any options are built, and Generate is then a no-op rather than a
/// null reference.
/// </para>
/// </remarks>
public partial class WbppExportWindow : ModalPageWindow<WbppExportWizardViewModel>
{
    public WbppExportWindow()
    {
        InitializeComponent();
    }

    /// <summary>The title bar and Alt+F4 share one predicate with Escape and Close,
    /// <c>CloseCommand.CanExecute</c>: refused only during a script commit. A copy carries on in
    /// the status bar.</summary>
    protected override bool RefuseClose(WindowCloseReason reason)
        => Page is { } wizard && !wizard.CloseCommand.CanExecute(null);

    /// <summary>Escape closes the wizard on the same predicate, <c>CloseCommand.CanExecute</c>:
    /// refused only during a script commit. It is swallowed either way so the platform does not
    /// close the modal behind the wizard's back.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Page is { } wizard && wizard.CloseCommand.CanExecute(null))
            {
                wizard.CloseCommand.Execute(null);
            }

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Page?.Page is { } page)
        {
            page.ScriptDestinationPicker = PickScriptDestinationAsync;
            page.StagingFolderPicker = PickStagingFolderAsync;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Page?.Page is { } page)
        {
            if (page.ScriptDestinationPicker == PickScriptDestinationAsync)
            {
                page.ScriptDestinationPicker = null;
            }

            if (page.StagingFolderPicker == PickStagingFolderAsync)
            {
                page.StagingFolderPicker = null;
            }
        }
    }

    // The staging field commits on focus loss, which is spec 12.13's "by the picker returning or
    // by the typed field losing focus". The rule that decides whether it is stored is the page's.
    private void OnStagingPathLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if ((sender as Control)?.DataContext is DestinationStep step)
        {
            step.Page.CommitStagingCommand.Execute(null);
        }
    }

    /// <summary>
    /// Opens the platform save dialog and returns the chosen absolute path, or null when the user
    /// cancelled, when there is no top level, or when the chosen location is not on the
    /// filesystem.
    /// </summary>
    /// <param name="suggestedFileName">The name the chosen flavour produced. The view never
    /// re-derives it: it depends on the flavour, which the page decided.</param>
    private async Task<string?> PickScriptDestinationAsync(string suggestedFileName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        IStorageFile? file;
        try
        {
            var start = await StartLocationAsync(storage).ConfigureAwait(true);
            file = await storage.SaveFilePickerAsync(SaveOptions(suggestedFileName, start))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A storage-provider fault is logged and treated as "no destination" rather than
            // taking the window down, the same shape both shipped pickers use.
            Log.Warning(ex, "The export script save dialog could not be opened");
            return null;
        }

        // TryGetLocalPath is null for a non-filesystem location. Null means "no destination" and
        // the command returns; nothing tries to resolve it into something writable.
        return file?.TryGetLocalPath();
    }

    /// <summary>Opens the platform folder picker and returns the chosen absolute path, or null on
    /// the same three conditions.</summary>
    private async Task<string?> PickStagingFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        try
        {
            var start = await StartLocationAsync(storage).ConfigureAwait(true);
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a staging folder",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            }).ConfigureAwait(true);

            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The staging folder picker could not be opened");
            return null;
        }
    }

    /// <summary>
    /// Where both dialogs open: the staging folder when it is set and the provider can answer for
    /// it, and the user's Documents folder otherwise.
    /// </summary>
    /// <remarks>Verification E2 and HANDOFF 5.2 item 43: <b>never a scan root</b>, and never a
    /// path this application composed or stored beyond the staging value the user themselves
    /// chose. A dialog with no start location opens wherever any picker in the process was last
    /// used, which in this application is in practice a scan root.</remarks>
    private async Task<IStorageFolder?> StartLocationAsync(IStorageProvider storage)
    {
        if (Page?.Page.StagingFolderText is { Length: > 0 } staging)
        {
            var folder = await storage.TryGetFolderFromPathAsync(staging).ConfigureAwait(true);
            if (folder is not null)
            {
                return folder;
            }
        }

        return await SaveDialogStart.DocumentsAsync(storage).ConfigureAwait(true);
    }

    /// <summary>
    /// The save dialog's options, built apart from the dialog so a case can read what they carry,
    /// exactly as <c>LogViewerView.SaveOptions</c> is.
    /// </summary>
    /// <param name="suggestedFileName">The generated script's name, which the page derived from
    /// the chosen flavour.</param>
    /// <param name="startLocation">Where the dialog opens. Null leaves the platform's own default,
    /// which is what a platform with no Documents folder gets.</param>
    internal static FilePickerSaveOptions SaveOptions(
        string suggestedFileName,
        IStorageFolder? startLocation)
    {
        var isShell = suggestedFileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
        var isReport = suggestedFileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        return new FilePickerSaveOptions
        {
            Title = isReport ? "Save the copy report" : "Save the export script",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = isReport ? "txt" : isShell ? "sh" : "ps1",
            SuggestedStartLocation = startLocation,

            // The operating system's own overwrite confirmation, which spec 12.13 names as the
            // only way an existing file is replaced. The application never chooses the path itself.
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                isReport
                    ? new FilePickerFileType("Text report") { Patterns = ["*.txt"] }
                    : isShell
                    ? new FilePickerFileType("Shell script") { Patterns = ["*.sh"] }
                    : new FilePickerFileType("PowerShell script") { Patterns = ["*.ps1"] },
            ],
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
