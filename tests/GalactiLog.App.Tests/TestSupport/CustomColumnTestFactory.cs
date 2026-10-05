using System.Collections.Concurrent;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place this assembly builds a custom column definition or a cell for a case (spec 12.15,
/// Phase 20 Task 3 brief section 6). Tasks 4, 5a, 5b, 6a, 6b and 6c all use it; a unit that builds
/// its own definition inline is a duplicate (design lesson 1).
/// </summary>
/// <remarks>
/// No database anywhere in here. The cell writes through a delegate, which is the seam the
/// view-model takes so it drives with no repository (spec 18.3), and the debounce is the same
/// <c>FakeDelay</c> the autosave cases already park and release rather than a second fake of its
/// own.
/// </remarks>
internal static class CustomColumnTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Every definition carries this creation moment, so the display order is what orders
    /// a set and the tiebreak is never the thing under test.</summary>
    private static readonly DateTime Created = new(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>What a write delegate answers when a case does not care.</summary>
    public static readonly CustomWriteResult Written = new(CustomWriteStatus.Written, Message: null);

    /// <summary>A check box column (spec 12.15's <c>boolean</c> type).</summary>
    public static CustomColumnDefinition Boolean(
        string name = "Done", int order = 0, CustomColumnScope scope = CustomColumnScope.Target)
        => Define(name, CustomColumnType.Boolean, scope, [], order);

    /// <summary>A text column.</summary>
    public static CustomColumnDefinition Text(
        string name = "Notes", int order = 0, CustomColumnScope scope = CustomColumnScope.Target)
        => Define(name, CustomColumnType.Text, scope, [], order);

    /// <summary>A dropdown column named "Priority", carrying the options in the order given.
    /// </summary>
    public static CustomColumnDefinition Dropdown(params string[] options)
        => Define("Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, options, order: 0);

    /// <summary>The general shape, for a case that needs a scope, an order or a type the three
    /// helpers above do not spell.</summary>
    public static CustomColumnDefinition Define(
        string name,
        CustomColumnType type,
        CustomColumnScope scope,
        IReadOnlyList<string> options,
        int order = 0)
        => new(
            Guid.NewGuid(),
            name,
            CustomColumnSlug.Base(name),
            type,
            scope,
            options,
            order,
            Created,
            ValueCount: 0);

    /// <summary>
    /// One cell over <paramref name="column"/>, wired to a recording delegate and an instantaneous
    /// debounce. A case that is about the debounce passes a <c>FakeDelay</c> as
    /// <paramref name="delay"/> and releases it itself.
    /// </summary>
    public static CustomValueViewModel Cell(
        CustomColumnDefinition column,
        string? stored = null,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? write = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string subject = "NGC 7000",
        CustomValueKey? key = null,
        Action<Action>? post = null)
        => new(
            column,
            key ?? CustomValueKey.ForTarget(Guid.NewGuid()),
            stored,
            subject,
            write ?? ((_, _, _) => Written),
            delay ?? Instant,
            post ?? Inline);

    /// <summary>Awaits the cell's pending write, so no case blocks and none sleeps
    /// (<c>xUnit1031</c> is an error).</summary>
    public static Task SettleAsync(CustomValueViewModel cell) => cell.PendingWrite.WaitAsync(Budget);

    /// <summary>The post seam. No dispatcher in most of these cases, so the closure runs on
    /// whatever thread reached it, the same shape <c>AutosaveFieldTests</c> uses.</summary>
    public static void Inline(Action action) => action();

    /// <summary>A debounce window that is already over, for the two kinds that commit at once and
    /// for a text case that is not about the window.</summary>
    public static Task Instant(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>One call of the write delegate, in the order it reached it.</summary>
    public sealed record WriteCall(Guid ColumnId, CustomValueKey Key, string? Value);

    /// <summary>The recording write delegate every cell case binds: what reached it, and what it
    /// answers.</summary>
    public sealed class WriteLog
    {
        private readonly ConcurrentQueue<WriteCall> _calls = new();

        /// <summary>What the next write answers. Defaults to accepting.</summary>
        public Func<string?, CustomWriteResult> Answer { get; set; } = _ => Written;

        public IReadOnlyList<WriteCall> Calls => [.. _calls];

        /// <summary>Just the values, which is what most cases assert.</summary>
        public IReadOnlyList<string?> Values => [.. _calls.Select(call => call.Value)];

        /// <summary>Bind this as the cell's <c>write</c>.</summary>
        public CustomWriteResult Write(Guid columnId, CustomValueKey key, string? value)
        {
            _calls.Enqueue(new WriteCall(columnId, key, value));
            return Answer(value);
        }
    }
}
