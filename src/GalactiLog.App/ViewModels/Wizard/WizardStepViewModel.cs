using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Wizard;

/// <summary>
/// One step of a <see cref="WizardViewModel{TStep}"/>. The wizard owns the order, the header, the
/// footer and the navigation; a step owns its own fields, its own inline validation and its help
/// topic.
/// </summary>
public abstract partial class WizardStepViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<Action> _post;

    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>; a unit
    /// test runs the closure inline.</param>
    /// <param name="logger">Optional. Background work a step starts logs here and never rethrows
    /// onto the UI thread.</param>
    protected WizardStepViewModel(Action<Action>? post = null, ILogger? logger = null)
    {
        _post = post ?? UiPost.Default;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The step's name, as the header renders it after "Step N of M: ".</summary>
    public abstract string Title { get; }

    /// <summary>The help topic the frame's heading glyph shows while this step is on screen.
    /// </summary>
    public abstract string HelpTopicId { get; }

    /// <summary>
    /// Whether this step's own rules allow Next. The wizard also refuses while busy or committed;
    /// this is the step's half of <c>CanGoNext</c>.
    /// </summary>
    public virtual bool CanAdvance => true;

    /// <summary>
    /// Called on the UI thread each time the wizard navigates onto this step. A step whose copy
    /// names something chosen on an earlier step refreshes it here rather than recomputing it on
    /// every property read.
    /// </summary>
    public virtual void OnEntered()
    {
    }

    /// <summary>
    /// A gate in front of the wizard's advance, or null to proceed. A returned message is shown as
    /// the step error and the wizard stays on the step.
    /// </summary>
    /// <remarks>Called on the UI thread only when <see cref="CanAdvance"/> is already true. A step
    /// that returns a one-time message must remember it did, or Next can never advance.</remarks>
    public virtual string? BeforeAdvance() => null;

    /// <summary>Cancelled when the step is disposed. Every background read a step starts is
    /// linked to it.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <summary>A step's logger, never null.</summary>
    protected ILogger Logger { get; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    protected internal bool IsDisposed { get; private set; }

    /// <summary>Reaches the UI thread through the injected seam.</summary>
    protected void Post(Action action) => _post(action);

    /// <summary>Raises <see cref="CanAdvance"/> so the wizard re-evaluates its Next command.
    /// </summary>
    protected void RaiseCanAdvanceChanged() => OnPropertyChanged(nameof(CanAdvance));

    /// <summary>Released by the derived step. The base cancels the step lifetime first, so
    /// nothing a step started can publish into a disposed wizard.</summary>
    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _lifetime.Cancel();
        DisposeCore();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
