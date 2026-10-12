using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Theme;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

public class FontManagerTests
{
    [Fact]
    public void Options_AreTheFiveFamilies_InterFirst()
    {
        Assert.Equal(
            ["inter", "atkinson", "montserrat", "ibm-plex-sans", "source-sans-3"],
            FontManager.Options.Select(option => option.Id));
        Assert.Equal("Inter", FontManager.Options[0].FontFamily.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("comic-sans")]
    public void Resolve_AnUnknownId_FallsBackToInter(string? id)
        => Assert.Equal("inter", FontManager.Resolve(id).Id);

    [Fact]
    public void Resolve_AShippedId_ReturnsIt()
        => Assert.Equal("montserrat", FontManager.Resolve("montserrat").Id);

    // Controls.axaml's Window style reads FontSans through a StaticResource, so this is what fails
    // if the live switch stops reaching a window that is already open.
    [AvaloniaFact]
    public void Apply_ReachesAnOpenWindow()
    {
        var window = new Window();
        window.Show();
        try
        {
            FontManager.Apply("montserrat");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Montserrat", window.FontFamily.Name);
        }
        finally
        {
            FontManager.Apply("inter");
            window.Close();
        }
    }
}
