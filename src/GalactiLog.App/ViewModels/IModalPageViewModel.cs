namespace GalactiLog.App.ViewModels;

/// <summary>
/// A page shown in its own modal window, which asks to be closed rather than closing itself
/// (design-spec 18.3: a view-model reaches no window). The window answers
/// <see cref="CloseRequested"/> by closing with the result the page passed.
/// </summary>
/// <remarks>
/// The one member the three modal pages share, and the whole reason
/// <c>Views.ModalPageWindow{TViewModel}</c> can own the subscription for all of them (FIXER LIST
/// F20). <c>PreviewModalViewModel</c> stays outside it: its close carries no result, so it has no
/// dialog result to hand back and no veto policy to declare.
/// </remarks>
public interface IModalPageViewModel
{
    /// <summary>Raised when the page is finished. The argument is the dialog result: whether the
    /// page's own action ran.</summary>
    event EventHandler<bool>? CloseRequested;
}
