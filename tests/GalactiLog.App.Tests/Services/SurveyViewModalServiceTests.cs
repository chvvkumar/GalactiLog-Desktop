using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.Core.Survey;
using Xunit;

namespace GalactiLog.App.Tests.Services;

public class SurveyViewModalServiceTests
{
    // Tier 1. A failure is a closed window whose fetch keeps running on an undisposed page.
    [AvaloniaFact]
    public async Task ClosingTheWindow_DisposesThePage_AndCancelsItsFetch()
    {
        var owner = new Window { Width = 1280, Height = 900 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        var never = new TaskCompletionSource<SurveyImageResult>();
        var rig = new SurveyViewRig { Respond = _ => never.Task };
        SurveyViewViewModel? page = null;
        var service = new SurveyViewModalService(_ => page = rig.Build(), new ModalHost(() => owner));

        var shown = service.ShowAsync(SurveyViewRig.M31);
        Dispatcher.UIThread.RunJobs();
        await rig.WaitForFetches(1);

        page!.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await shown;

        Assert.True(rig.Fetches[0].Token.IsCancellationRequested);

        owner.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
