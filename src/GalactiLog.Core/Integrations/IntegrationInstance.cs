namespace GalactiLog.Core.Integrations;

/// <summary>One stored <c>nina_instances</c> or <c>stellarium_instances</c> entry (design-spec
/// 5.8.1, 12.7). Nothing is dropped on read for being blank or unparseable: the External Tools tab
/// lists such a row so the reader can repair what is stored.</summary>
/// <param name="Name">The instance name as stored, untrimmed; <c>""</c> when the stored entry had
/// no readable <c>name</c>.</param>
/// <param name="Url">The instance URL as stored; <c>""</c> when the stored entry had no readable
/// <c>url</c>.</param>
/// <param name="Enabled">The stored <c>enabled</c> flag; false when it was absent or not a
/// bool.</param>
public sealed record IntegrationInstance(string Name, string Url, bool Enabled)
{
    /// <summary>True when this instance is offered on the Target page's menu and captioned as
    /// offered on the External Tools tab: enabled, a non-blank trimmed name, and a URL that passes
    /// <see cref="IntegrationUrl.TryParse"/>. One declaration of the predicate, because the tab's
    /// caption and the menu's filter are the two halves of one rule (spec 12.7).</summary>
    public static bool IsOffered(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Enabled
            && !string.IsNullOrWhiteSpace(instance.Name)
            && IntegrationUrl.TryParse(instance.Url, out _);
    }
}
