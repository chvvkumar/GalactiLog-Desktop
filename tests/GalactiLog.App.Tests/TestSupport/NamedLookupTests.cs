using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace GalactiLog.App.Tests.TestSupport;

public sealed class NamedLookupTests
{
    private static Button In(Control host, string name)
    {
        var button = new Button { Name = name };
        NameScope.GetNameScope(host)!.Register(name, button);
        ((Panel)host).Children.Add(button);
        return button;
    }

    private static Grid Scoped()
    {
        var grid = new Grid();
        NameScope.SetNameScope(grid, new NameScope());
        return grid;
    }

    private static (Grid Root, Grid Inner) Nested()
    {
        var root = Scoped();
        var inner = Scoped();
        var uc = new UserControl { Content = inner };
        NameScope.SetNameScope(uc, NameScope.GetNameScope(inner)!);
        root.Children.Add(uc);
        return (root, inner);
    }

    [AvaloniaFact]
    public void Named_ElementInRootScope_ReturnsIt()
    {
        var root = Scoped();
        var button = In(root, "A");
        Assert.Same(button, root.Named<Button>("A"));
    }

    [AvaloniaFact]
    public void Named_ElementInNestedUserControlScope_ReturnsIt()
    {
        var (root, inner) = Nested();
        var button = In(inner, "B");
        Assert.Same(button, root.Named<Button>("B"));
    }

    [AvaloniaFact]
    public void Named_ElementInScopeOfNonUserControl_ReturnsIt()
    {
        var root = Scoped();
        var inner = Scoped();
        var host = new ContentControl { Content = inner };
        NameScope.SetNameScope(host, NameScope.GetNameScope(inner)!);
        root.Children.Add(host);
        var button = In(inner, "D");
        Assert.Same(button, root.Named<Button>("D"));
    }

    [AvaloniaFact]
    public void NamedOrNull_NothingMatchesInRootOrNestedScope_ReturnsNull()
    {
        var (root, inner) = Nested();
        In(root, "Other");
        In(inner, "Another");
        Assert.Null(root.NamedOrNull<Button>("Missing"));
    }

    [AvaloniaFact]
    public void NamedOrNull_ElementInNestedUserControlScope_ReturnsIt()
    {
        var (root, inner) = Nested();
        var button = In(inner, "C");
        Assert.Same(button, root.NamedOrNull<Button>("C"));
    }

    [AvaloniaFact]
    public void Named_NoElementMatches_Throws()
    {
        var (root, _) = Nested();
        Assert.ThrowsAny<Exception>(() => root.Named<Button>("Missing"));
    }

    [AvaloniaFact]
    public void Named_TwoElementsMatch_Throws()
    {
        var (root, inner) = Nested();
        In(root, "Twice");
        In(inner, "Twice");
        Assert.ThrowsAny<Exception>(() => root.Named<Button>("Twice"));
    }
}
