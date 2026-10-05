using Avalonia.Platform.Storage;
using Serilog;

namespace GalactiLog.App.Views;

/// <summary>
/// Where a save dialog opens. One definition, reached by every view that offers one.
/// </summary>
/// <remarks>
/// <para>
/// Verification E2, and the coordinator's ruling on its sibling. A platform save dialog with no
/// <see cref="FilePickerSaveOptions.SuggestedStartLocation"/> opens wherever any picker in the
/// process was last used, and in this application the other pickers are the Library tab's and the
/// setup wizard's folder pickers, so in practice that is a SCAN ROOT. Five default-named log files
/// landed in a fixture library that way during the launched-app verification. Both save dialogs
/// have the same hazard, so this is the second occurrence of the pattern and it is a shared spine
/// rather than a copied helper (design-lessons rule 1).
/// </para>
/// <para>
/// A static over the storage provider rather than a service: the two callers are views, which are
/// constructed by the XAML loader with no constructor arguments, and HANDOFF section 5 keeps
/// pickers as Avalonia storage-provider calls in the view rather than behind a new service. There
/// is no state to hold and nothing to inject.
/// </para>
/// </remarks>
internal static class SaveDialogStart
{
    /// <summary>
    /// The user's Documents folder, or null when the platform has no such folder or the provider
    /// cannot answer, which leaves the dialog's own default.
    /// </summary>
    /// <remarks>
    /// The storage provider's own well-known folder lookup, never
    /// <c>Environment.GetFolderPath</c> and never a path this application composed or stored: a
    /// path it composed is a path it could get wrong, and a folder the provider hands back is one
    /// the dialog can already open. A scan root is exactly the kind of stored path this refuses to
    /// reach for.
    /// </remarks>
    public static async Task<IStorageFolder?> DocumentsAsync(IStorageProvider storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        try
        {
            return await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Documents)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // A provider that cannot answer is not a reason to refuse the save: the dialog opens
            // at its own default instead. The static Serilog logger for the reason the views'
            // own picker handlers use it: a view has no injected one.
            Log.Warning(exception, "The Documents folder could not be resolved for a save dialog");
            return null;
        }
    }
}
