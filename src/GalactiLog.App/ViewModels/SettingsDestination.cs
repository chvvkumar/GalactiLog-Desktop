namespace GalactiLog.App.ViewModels;

/// <summary>
/// Where a notice elsewhere in the application sends a reader: a Settings tab, and a section
/// inside it. One enum carried by one routing event, rather than one event per destination.
/// </summary>
/// <remarks>
/// <para>
/// In the shell's own namespace rather than on the page that first needed it, because the shell is
/// what resolves a member to a tab and a section (<c>MainWindowViewModel.OpenSettingsAt</c>) and
/// because two unrelated pages now raise it: the Dashboard's scan filter notice (spec 12.2) and the
/// Statistics page's Guiding empty notice (spec 12.5). A Dashboard that had to name a type in
/// <c>ViewModels.Stats</c> to ask for a Settings tab would be the wrong dependency.
/// </para>
/// <para>
/// Phase 15B Task 5c, design lesson 1 at the third destination. Adding a fourth is a member here,
/// a switch arm in <c>MainWindowViewModel.OnOpenSettingsRequested</c> and the
/// <c>Request...InView()</c> member that arm names; nothing else.
/// </para>
/// </remarks>
public enum SettingsDestination
{
    /// <summary>Settings, the Library tab, at the "Read PHD2 guide logs" switch (spec 12.7).
    /// </summary>
    LibraryGuideLogSwitch,

    /// <summary>Settings, the Equipment tab, at the PHD2 profiles panel (spec 12.7).</summary>
    EquipmentPhd2Profiles,

    /// <summary>Settings, the Library tab, at the name rule editor (spec 12.2's Review action,
    /// spec 12.7's rule editor).</summary>
    LibraryNameRules,
}
