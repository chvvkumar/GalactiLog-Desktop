using Avalonia.Controls;

namespace GalactiLog.App.Controls;

/// <summary>
/// The one tooltip a drawn control retargets as the pointer crosses what it drew. Shared by
/// <see cref="NightStrip"/> and <see cref="GuideGraph"/>, which have no per-mark visual to attach
/// a tip to. Extracted at the second occurrence (Phase 15B Task 4a, brief section 4.3).
/// </summary>
internal static class DrawnTip
{
    // Pointer moves arrive by the hundred across one pass of a drawn control and most of them land
    // on the same mark as the one before, so the attached property is written only when the text
    // actually changes.
    public static void SetTip(Control control, ref string? last, string? text)
    {
        if (text == last)
        {
            return;
        }

        last = text;
        ToolTip.SetTip(control, text);
    }
}
