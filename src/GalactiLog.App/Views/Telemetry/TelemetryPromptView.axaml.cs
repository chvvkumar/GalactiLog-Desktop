using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Telemetry;

namespace GalactiLog.App.Views.Telemetry;

/// <summary>
/// The one-time telemetry question. Closing by the title bar, Alt+F4 or Escape is an answer: "no",
/// recorded as prompted. An application or OS shutdown closing it is not an answer and records
/// nothing, so the question comes back on the next start.
/// </summary>
public partial class TelemetryPromptView : ModalPageWindow<TelemetryPromptViewModel>
{
    public TelemetryPromptView()
    {
        InitializeComponent();

        // ModalPageWindow seals OnClosing; the Closing event is raised from its base call, so it
        // sees only closes that were not vetoed, with their reason. A close the page asked for
        // (Continue) has already decided, and Dismiss then does nothing.
        Closing += (_, e) =>
        {
            if (e.CloseReason == WindowCloseReason.WindowClosing)
            {
                _ = Page?.Dismiss();
            }
        };
    }

    /// <summary>Escape closes the prompt, which the Closing handler records as "no".</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Refuses nothing. Dismissing is the "no" answer.</summary>
    protected override bool RefuseClose(WindowCloseReason reason) => false;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
