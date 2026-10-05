using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Preview;

namespace GalactiLog.App.Views.Preview;

/// <summary>
/// Spec 12.4's Sky view window on the preview modal's shell. The window supplies the pointer and
/// key geometry; every fetch rule is on <see cref="SurveyViewViewModel"/>.
/// </summary>
public partial class SurveyViewWindow : Window
{
    private readonly Border? _viewport;

    private SurveyViewViewModel? _subscribed;
    private bool _panning;
    private Point _panStart;
    private Point _panLast;
    private double _wheel;

    public SurveyViewWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _viewport = this.FindControl<Border>("Viewport");
        if (_viewport is not null)
        {
            _viewport.PointerWheelChanged += OnViewportWheel;
            _viewport.PointerPressed += OnViewportPressed;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.CloseRequested -= OnCloseRequested;
        }

        _subscribed = DataContext as SurveyViewViewModel;

        if (_subscribed is not null)
        {
            _subscribed.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    /// <summary>The keyboard lands on the image host from the moment the window opens, as in the
    /// preview modal.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _viewport?.Focus(NavigationMethod.Unspecified);
    }

    /// <summary>Spec 12.4's plus and minus on the main row or the number pad, with or without
    /// Shift.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is SurveyViewViewModel page)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    page.Zoom(1);
                    e.Handled = true;
                    return;

                case Key.OemMinus:
                case Key.Subtract:
                    page.Zoom(-1);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is SurveyViewViewModel page && e.Delta.Y != 0)
        {
            // A touchpad sends fractional deltas, so they add up and a step is taken per whole notch.
            // A reversal starts a fresh count so a leftover part-notch cannot absorb the reverse notch.
            if (_wheel * e.Delta.Y < 0)
            {
                _wheel = 0;
            }

            _wheel += e.Delta.Y;
            var steps = (int)Math.Truncate(_wheel);
            if (steps != 0)
            {
                _wheel -= steps;
                page.Zoom(steps);
            }

            e.Handled = true;
        }
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            _panning = true;
            _panStart = _panLast = e.GetPosition(this);
        }
    }

    /// <summary>Tracked on the window so a drag that leaves the viewport keeps moving the image; a
    /// button released outside the window ends the drag at the last position seen.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_panning)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndPan(_panLast);
            }
            else if (DataContext is SurveyViewViewModel page)
            {
                var position = e.GetPosition(this);
                page.DragBy(position.X - _panLast.X, position.Y - _panLast.Y);
                _panLast = position;
            }
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            EndPan(e.GetPosition(this));
        }

        base.OnPointerReleased(e);
    }

    // The drawn side is the viewport's short side times the scale, because the image is square
    // and Stretch="Uniform" fits it to that side.
    private void EndPan(Point end)
    {
        _panning = false;
        if (DataContext is not SurveyViewViewModel page || _viewport is null)
        {
            return;
        }

        var side = Math.Min(_viewport.Bounds.Width, _viewport.Bounds.Height) * page.Scale;
        if (side > 0)
        {
            page.CommitPan((end.X - _panStart.X) / side, (end.Y - _panStart.Y) / side);
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
