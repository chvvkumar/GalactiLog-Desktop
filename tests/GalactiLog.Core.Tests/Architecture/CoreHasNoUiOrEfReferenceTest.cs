using System.Linq;
using System.Reflection;
using Xunit;

namespace GalactiLog.Core.Tests.Architecture;

public class CoreHasNoUiOrEfReferenceTest
{
    [Fact]
    public void CoreAssembly_ReferencesNoAvaloniaOrEfCore()
    {
        var assembly = Assembly.Load("GalactiLog.Core");
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(referenced, name =>
            name is not null &&
            (name.StartsWith("Avalonia", System.StringComparison.Ordinal) ||
             name.StartsWith("Microsoft.EntityFrameworkCore", System.StringComparison.Ordinal) ||
             name.StartsWith("Microsoft.Data.Sqlite", System.StringComparison.Ordinal)));
    }
}
