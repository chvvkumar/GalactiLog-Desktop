using Avalonia.Controls;

namespace GalactiLog.App.Controls;

/// <summary>Draws <see cref="Services.Notifier.Notices"/>. Its DataContext is the notifier.
/// </summary>
public partial class NoticeHost : UserControl
{
    public NoticeHost() => InitializeComponent();
}
