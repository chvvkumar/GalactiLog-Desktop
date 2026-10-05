namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Core-shapes 2.9's one declaration of the filter-name union spec 12.7 asks for: the canonical
/// names of the Filters tab's grouping editor plus its ungrouped discovered names, de-duplicated
/// and sorted with <see cref="StringComparer.OrdinalIgnoreCase"/>. <see cref="FiltersTabViewModel"/>
/// and the External Tools tab both read this one method, in the shape
/// <c>EquipmentTabViewModel.KnownTelescopes()</c> already has, so the two lists cannot drift
/// (design-lessons rule 1).
/// </summary>
public static class FilterNameUnion
{
    public static IReadOnlyList<string> From(GroupingEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in editor.Groups)
        {
            names.Add(group.Canonical);
        }

        foreach (var row in editor.Ungrouped)
        {
            names.Add(row.Name);
        }

        return [.. names];
    }
}
