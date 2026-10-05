namespace GalactiLog.App.ViewModels;

/// <summary>
/// One item of a collapsed 48 pixel vertical strip (DESIGN.md section 6, "The collapsed vertical
/// strip"). Three members, which is everything Theme/Controls.axaml's StripItemTemplate binds, so
/// the Nights ledger's strip and the Dashboard filter panel's strip render from one template
/// rather than from two hand copies (Phase 14C ruling E2, design lesson 1).
/// </summary>
/// <remarks>
/// <see cref="IsMonospace"/> is the whole difference between the two hosts: the ledger's labels are
/// dates in a tabular form ("09-08") and render in TextBlock.mono, and a filter section's short
/// label ("Type") must not. A shared template that dropped mono would silently restyle a shipped
/// page and pass every other case in the suite.
/// </remarks>
public interface IStripItem
{
    /// <summary>The label the strip draws, at most four characters wide at the Extra Large text size
    /// (fixer-list item 18). The Nights ledger is the exception (polish wave 9 ruling 3): the full
    /// date, because a clipped "09-08" names no year, and its host is sized to it.</summary>
    string ShortLabel { get; }

    /// <summary>The full label, shown on the item's tooltip.</summary>
    string FullLabel { get; }

    /// <summary>The StreamGeometry resource the strip draws instead of <see cref="ShortLabel"/>
    /// (polish wave 1 ruling 6), or null to draw the label.</summary>
    string? IconKey { get; }

    /// <summary>True when the label renders in the monospace face.</summary>
    bool IsMonospace { get; }
}
