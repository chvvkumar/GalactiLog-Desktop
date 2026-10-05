using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace GalactiLog.App.Tests.TestSupport;

public static class NamedLookup
{
    public static T Named<T>(this Control root, string name) where T : class
        => Search<T>(root, name) ?? throw new InvalidOperationException(
            $"No {typeof(T).Name} named '{name}' in any name scope below the root.");

    public static T? NamedOrNull<T>(this Control root, string name) where T : class
        => Search<T>(root, name);

    private static T? Search<T>(Control root, string name) where T : class
    {
        var scopes = new List<INameScope>();
        if (root.FindNameScope() is { } own) scopes.Add(own);
        var below = root.GetLogicalDescendants().Cast<object>()
            .Concat(root.GetVisualDescendants()).OfType<StyledElement>().Distinct();
        foreach (var element in below)
            if (NameScope.GetNameScope(element) is { } scope && !scopes.Contains(scope)) scopes.Add(scope);

        var found = scopes.Select(s => s.Find(name)).OfType<T>().Distinct().ToList();
        return found.Count > 1
            ? throw new InvalidOperationException($"{found.Count} elements named '{name}' below the root; the name must be unique.")
            : found.FirstOrDefault();
    }
}
