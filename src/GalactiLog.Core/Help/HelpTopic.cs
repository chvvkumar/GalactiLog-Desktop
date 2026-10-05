namespace GalactiLog.Core.Help;

/// <summary>
/// One row of spec 12.12's contextual help table: the topic id a heading names, the title the
/// flyout puts at the top, and the paragraph under it.
/// </summary>
/// <param name="Id">The id a <c>HelpButton</c> carries in markup, matched ordinally.</param>
/// <param name="Title">The heading's own name, also the tail of the accessible name.</param>
/// <param name="Paragraph">One to five sentences of plain text with no markup. Line breaks inside
/// it are not significant, so the flyout wraps it to its own width.</param>
public sealed record HelpTopic(string Id, string Title, string Paragraph);
