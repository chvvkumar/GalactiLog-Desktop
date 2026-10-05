using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>What every layout view shares: the page it follows while attached, the ledger's scroll
/// to a night, the lanes handle's flush before the page is let go, and the frames region's least
/// height. A layout supplies its lanes limits and its own page and release hooks.</summary>
public abstract class TargetLayoutView : UserControl
{
    private LanesHandle? _handle;

    private NightsLedgerPart? _ledger;

    private Control? _framesRegion;

    private FramesPart? _framesPart;

    /// <summary>The page this view follows; null while detached.</summary>
    protected TargetDetailViewModel? Page { get; private set; }

    /// <summary>The least the frames region keeps: its chrome and one row; the default text size's
    /// figure until a night with frames is measured.</summary>
    public double FramesMinHeight { get; private set; } = 300d;

    /// <summary>Wires the shared pieces; a layout calls it once from its constructor.</summary>
    protected void UseSpine(string layoutKey, NightsLedgerPart ledger, ScrollViewer lanes, LanesHandle handle, Control framesRegion, FramesPart framesPart)
    {
        _ledger = ledger;
        _handle = handle;
        _framesRegion = framesRegion;
        _framesPart = framesPart;
        framesRegion.LayoutUpdated += (_, _) => MeasureFrames();
        handle.Attach(lanes, layoutKey, () => Page?.TargetPage, LanesLimits);
    }

    protected abstract LanesHandle.Limits LanesLimits();

    /// <summary>A page property other than the ledger's scroll target changed.</summary>
    protected virtual void OnPageChanged(TargetDetailViewModel page, string? propertyName)
    {
    }

    /// <summary>Runs once a page is followed, after the lanes handle has placed itself from it.</summary>
    protected virtual void OnPageAttached(TargetDetailViewModel page)
    {
    }

    /// <summary>Runs on detach after the handle's flush and before the page is let go.</summary>
    protected virtual void OnReleasing()
    {
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty && this.IsAttachedToVisualTree())
        {
            Subscribe(DataContext as TargetDetailViewModel);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(DataContext as TargetDetailViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _handle?.Flush();
        OnReleasing();
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void MeasureFrames()
    {
        if (_framesRegion is null || _framesPart is null || _handle is null || !_framesRegion.IsEffectivelyVisible)
        {
            return;
        }

        if (_handle.MeasureFrames(_framesRegion, _framesPart) is { Chrome: var chrome, Row: var row } && Math.Abs((chrome + row) - FramesMinHeight) > 0.5d)
        {
            FramesMinHeight = chrome + row;
            _handle.Refresh();
        }
    }

    private void Subscribe(TargetDetailViewModel? page)
    {
        if (ReferenceEquals(page, Page))
        {
            return;
        }

        if (Page is not null)
        {
            Page.PropertyChanged -= OnPagePropertyChanged;
        }

        Page = page;
        if (page is not null)
        {
            page.PropertyChanged += OnPagePropertyChanged;
            ScrollLedgerTo(page);
            _handle?.Refresh();
            OnPageAttached(page);
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TargetDetailViewModel page)
        {
            return;
        }

        if (e.PropertyName == nameof(TargetDetailViewModel.LedgerScrollTarget))
        {
            ScrollLedgerTo(page);
        }
        else
        {
            OnPageChanged(page, e.PropertyName);
        }
    }

    private void ScrollLedgerTo(TargetDetailViewModel page)
    {
        if (page.LedgerScrollTarget is { } night)
        {
            _ledger?.ScrollToNight(night);
        }
    }
}
