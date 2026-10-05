using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// The frame table view shares the view model's SelectedRows with its ListBox. A view leaving the
// tree must stop sharing it before its context goes, or releasing the view clears the selection.
public class FrameTableViewReleaseTests
{
    private static readonly DateOnly Night = new(2025, 12, 7);

    private static FrameTableViewModel NewTable(int count = 12) => NightPartsTestKit.Table(NightPartsTestKit.Frames(count, Night));

    private static (Window Window, ContentControl Host) Host(FrameTableView view, object context)
    {
        var host = new ContentControl { DataContext = context, Content = view };
        var window = new Window { Width = 1280, Height = 800, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, host);
    }

    private static bool ShownSelected(FrameTableView view, FrameRowViewModel row)
    {
        var list = view.Named<ListBox>("FrameRows");
        return list.SelectedItems!.Cast<object>().SequenceEqual([row])
            && list.ContainerFromItem(row) is ListBoxItem { IsSelected: true };
    }

    [AvaloniaFact]
    public void AReleasedView_AttachedAgainTwice_KeepsAndShowsTheSelection()
    {
        // A failure looks like the release emptying SelectedRows, or the same view instance coming
        // back with nothing lit, or throwing on the second attach.
        var table = NewTable();
        var view = new FrameTableView();
        var (window, host) = Host(view, table);
        table.SelectFrameAt(3);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(table.SelectedRows);

        for (var round = 0; round < 2; round++)
        {
            host.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(row, Assert.Single(table.SelectedRows));

            host.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(row, Assert.Single(table.SelectedRows));
            Assert.True(ShownSelected(view, row));
        }

        window.Close();
    }

    [AvaloniaFact]
    public void TheContextMovingToAnotherTable_LeavesTheOldSelection_AndShowsTheNewTablesOwn()
    {
        // A night switch, and a rescan that builds a second table for the same night, both move one
        // view to another table. The table it left keeps its selection, as it always did; the new
        // table's own selection is kept and lit. A failure looks like either table losing its rows.
        foreach (var rescan in new[] { false, true })
        {
            var left = NewTable();
            var next = rescan ? NewTable() : NightPartsTestKit.Table(NightPartsTestKit.Frames(8, Night.AddDays(-1)));
            var view = new FrameTableView { DataContext = left };
            var window = new Window { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            left.SelectFrameAt(3);
            next.SelectFrameAt(5);
            Dispatcher.UIThread.RunJobs();
            var leftRow = Assert.Single(left.SelectedRows);
            var nextRow = Assert.Single(next.SelectedRows);

            view.DataContext = next;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(leftRow, Assert.Single(left.SelectedRows));
            Assert.Same(nextRow, Assert.Single(next.SelectedRows));
            Assert.True(ShownSelected(view, nextRow));
            window.Close();
        }
    }
}
