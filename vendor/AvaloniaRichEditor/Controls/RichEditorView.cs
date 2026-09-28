using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AvaloniaRichEditor.Controls;

/// <summary>Editor with a connected toolbar and scrolling viewport. Use the separate controls for custom layouts.</summary>
public class RichEditorView : UserControl
{
    public RichEditor Editor { get; } = new();

    public RichEditorToolbar Toolbar { get; }

    /// <inheritdoc cref="ZoomFactor"/>
    public static readonly StyledProperty<double> ZoomFactorProperty =
        AvaloniaProperty.Register<RichEditorView, double>(nameof(ZoomFactor), 1.0, coerce: CoerceZoom);

    /// <summary>Visual zoom for the document area (1.0 = 100%). The toolbar is never scaled. Scaling
    /// is applied around the editor, which reflows to the zoomed width — text stays crisp and no
    /// horizontal scrollbar appears in the continuous layout. Clamped to 0.2–5.0.</summary>
    public double ZoomFactor
    {
        get => GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    private static double CoerceZoom(AvaloniaObject _, double v)
        => double.IsFinite(v) ? Math.Clamp(v, 0.2, 5.0) : 1.0;

    /// <inheritdoc cref="FitToWidth"/>
    public static readonly StyledProperty<bool> FitToWidthProperty =
        AvaloniaProperty.Register<RichEditorView, bool>(nameof(FitToWidth), true);

    /// <summary>When <see langword="true"/> (the default), the view auto-scales the document so the
    /// page (or fixed content column) exactly fills the viewport width, recomputing on resize and on
    /// paper/orientation/outline changes; no horizontal scrollbar appears. Setting <see cref="ZoomFactor"/>
    /// explicitly (e.g. a zoom control) turns this off. The continuous layout always fits at 1.0.</summary>
    public bool FitToWidth
    {
        get => GetValue(FitToWidthProperty);
        set => SetValue(FitToWidthProperty, value);
    }

    // Guards the self-driven ZoomFactor write in ApplyFitWidth so it isn't mistaken for an explicit
    // (fit-cancelling) zoom from a host.
    private bool _settingZoomInternally;

    private static string Loc(string key) => AvaloniaRichEditor.RichEditorLocalization.GetString(key);

    /// <inheritdoc cref="ShowStatusBar"/>
    public static readonly StyledProperty<bool> ShowStatusBarProperty =
        AvaloniaProperty.Register<RichEditorView, bool>(nameof(ShowStatusBar), true);

    /// <summary>Whether the built-in bottom status bar (character/word counts, caret line/column,
    /// page count and the soft image-limit warning) is shown. Default <see langword="true"/>.</summary>
    public bool ShowStatusBar
    {
        get => GetValue(ShowStatusBarProperty);
        set => SetValue(ShowStatusBarProperty, value);
    }

    /// <inheritdoc cref="ShowFileActions"/>
    public static readonly StyledProperty<bool> ShowFileActionsProperty =
        AvaloniaProperty.Register<RichEditorView, bool>(nameof(ShowFileActions), true);

    /// <summary>Whether the built-in Export/Import (and Print, when <see cref="PrintRequested"/> is
    /// handled) buttons are shown at the end of the toolbar. Export/Import use the platform file picker
    /// for JSON/.flow/HTML. Default <see langword="true"/>.</summary>
    public bool ShowFileActions
    {
        get => GetValue(ShowFileActionsProperty);
        set => SetValue(ShowFileActionsProperty, value);
    }

    private TextBlock _statusInfo = null!, _pageInfo = null!, _limitInfo = null!;
    private Border _statusBar = null!;

    private EventHandler? _printRequested;

    /// <summary>Raised when the user clicks the toolbar's built-in Print button. Printing is platform-specific
    /// (and intentionally not implemented in this cross-platform library), so a host handles this to
    /// drive its own print/preview. The Print button is hidden until at least one handler is attached.</summary>
    public event EventHandler? PrintRequested
    {
        add
        {
            bool had = _printRequested != null;
            _printRequested += value;
            if (!had && _printRequested != null) Toolbar.PrintRequested += ForwardPrint;
        }
        remove
        {
            _printRequested -= value;
            if (_printRequested == null) Toolbar.PrintRequested -= ForwardPrint;
        }
    }

    private void ForwardPrint(object? sender, EventArgs e) => _printRequested?.Invoke(this, e);

    // LayoutTransform keeps scroll extent and reflow in sync with zoom. Top-align to prevent short notes from centering.
    private readonly LayoutTransformControl _zoomHost;
    private readonly ScrollViewer _scroller;

    public void ScrollToTop() => _scroller.Offset = new Vector(0, 0);

    /// <summary>The document viewport offset, in scaled pixels.</summary>
    public Vector ScrollOffset
    {
        get => _scroller.Offset;
        set => _scroller.Offset = value;
    }

    public RichEditorView()
    {
        Toolbar = new RichEditorToolbar { Target = Editor, ToolbarLevel = ToolbarLevel.Maximum };
        // Zoom is view-level here (a LayoutTransform around the editor), so the toolbar's zoom combo is
        // driven through these hooks rather than the editor directly.
        Toolbar.ZoomGetter = () => ZoomFactor;
        Toolbar.IsFitWidthGetter = () => FitToWidth;
        Toolbar.ZoomSetter = ZoomToPercent;
        Toolbar.FitWidthAction = () => SetCurrentValue(FitToWidthProperty, true);

        Editor.ShowPageBoundaries = false;

        // Use editor margins rather than scroller padding to avoid clipping; the right gutter clears the idle scrollbar.
        Editor.Margin = new Thickness(12, 12, 18, 12);

        _zoomHost = new LayoutTransformControl
        {
            Child = Editor,
            LayoutTransform = new ScaleTransform(1, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _scroller = new ScrollViewer
        {
            Content = _zoomHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // The editor scrolls its caret explicitly. Scrolling the whole control on focus
            // hides its top margin and shifts the text under the first pointer click.
            BringIntoViewOnFocusChange = false,
        };
        UpdateHorizontalScroll();
        // Make the editor at least as tall as the viewport, so the empty area below short content is part
        // of the editing surface (click-to-end) and the "draw table" rubber-band can extend into it without
        // being clipped to the content height. MinHeight is in editor (pre-zoom) px, so divide by the zoom.
        _scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty) UpdateEditorFillHeight();
        };
        // A concrete paper size fixes the column width, so a narrow viewport (or a zoomed page) can
        // exceed it → allow horizontal scrolling there. The continuous (Free) layout reflows to the
        // viewport, so it must stay disabled (a finite width is what makes the editor reflow instead
        // of growing unbounded).
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == RichEditor.PageSizeProperty
                || e.Property == RichEditor.ShowPageBoundariesProperty
                || e.Property == RichEditor.PageOrientationProperty)
            {
                UpdateHorizontalScroll();
                ApplyFitWidth(); // paper/orientation/outline change the fit target
            }
        };
        SizeChanged += (_, _) => ApplyFitWidth();

        BuildStatusBar();

        var dock = new DockPanel();
        DockPanel.SetDock(Toolbar, Dock.Top);
        dock.Children.Add(Toolbar);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        dock.Children.Add(_statusBar);
        dock.Children.Add(_scroller);
        Content = dock;

        Toolbar.ShowFileActions = ShowFileActions;

        // Keep the status bar live. Counts follow any caret move; the page count and image-limit
        // warning ride the content-only signal (they need O(blocks) walks).
        Editor.SelectionChanged += (_, _) => UpdateCounts();
        Editor.TextChanged += (_, _) => UpdateStatus();
        Editor.RecommendedImageLimitExceeded += (_, _) => UpdateImageWarning();
        UpdateStatus();
    }

    // A horizontal scrollbar only makes sense for a fixed-width paged column that overflows the
    // viewport. In fit-to-width the column is scaled to the viewport, so it never overflows — and the
    // continuous layout reflows — so both disable it.
    private void UpdateHorizontalScroll()
        => _scroller.HorizontalScrollBarVisibility = (Editor.IsPaged && !FitToWidth)
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;

    // Fill the viewport for short notes. Cap the minimum height to avoid scrollbar/viewport feedback loops.
    private void UpdateEditorFillHeight()
    {
        double vh = _scroller.Viewport.Height;
        double zoom = ZoomFactor > 0 ? ZoomFactor : 1.0;
        if (vh > 0) Editor.MinHeight = vh / zoom;
    }

    // Scales the document so the page (chrome) or fixed content column (no chrome) fills the viewport
    // width. Mirrors the print/desk geometry: a chromed page adds a desk gap each side; a bare column
    // is the paper minus its 2×48 margins. Continuous reflows on its own, so fit is just 1.0.
    private void ApplyFitWidth()
    {
        if (!FitToWidth) return;
        double vw = Bounds.Width;
        if (vw < 50) return; // not laid out yet
        const double pad = 40;
        // Fit width must use the same desk gap as page rendering.
        const double deskGap = RichEditor.PageGap;
        double target;
        if (Editor.PageSize == RichEditorPageSize.Continuous)
            target = 0;
        else
        {
            double paperW = Editor.GetPaperPixelSize().Width;
            target = Editor.ShowPageBoundaries ? paperW + 2 * deskGap : paperW - 96;
        }
        double z = target > 0 ? Math.Clamp((vw - pad) / target, 0.2, 5.0) : 1.0;
        _settingZoomInternally = true;
        try { SetCurrentValue(ZoomFactorProperty, z); }
        finally { _settingZoomInternally = false; }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomFactorProperty)
        {
            _zoomHost.LayoutTransform = new ScaleTransform(ZoomFactor, ZoomFactor);
            // An explicit zoom (not our own fit write) cancels fit-to-width.
            if (!_settingZoomInternally) SetCurrentValue(FitToWidthProperty, false);
            Toolbar.RefreshPageControls(); // zoom is view-level, so push it onto the toolbar's zoom combo
        }
        else if (change.Property == FitToWidthProperty)
        {
            UpdateHorizontalScroll();
            if (FitToWidth) ApplyFitWidth();
            Toolbar.RefreshPageControls();
        }
        else if (change.Property == ShowStatusBarProperty)
        {
            if (_statusBar != null) _statusBar.IsVisible = ShowStatusBar;
        }
        else if (change.Property == ShowFileActionsProperty)
        {
            Toolbar.ShowFileActions = ShowFileActions;
        }
    }

    private void ZoomToPercent(double factor)
    {
        SetCurrentValue(FitToWidthProperty, false);
        SetCurrentValue(ZoomFactorProperty, Math.Clamp(factor, 0.2, 5.0));
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomToPercent(ZoomFactor + (e.Delta.Y > 0 ? 0.1 : -0.1));
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key is Key.D0 or Key.NumPad0) { SetCurrentValue(FitToWidthProperty, true); e.Handled = true; return; }
            if (e.Key is Key.OemPlus or Key.Add) { ZoomToPercent(ZoomFactor + 0.1); e.Handled = true; return; }
            if (e.Key is Key.OemMinus or Key.Subtract) { ZoomToPercent(ZoomFactor - 0.1); e.Handled = true; return; }
        }
        base.OnKeyDown(e);
    }


    private void BuildStatusBar()
    {
        TextBlock Tb(string color) => new TextBlock
        {
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusInfo = Tb("#444444");
        _pageInfo = Tb("#444444");
        _pageInfo.Margin = new Thickness(0, 0, 12, 0);
        _limitInfo = Tb("#CC6600");
        _limitInfo.Margin = new Thickness(0, 0, 12, 0);

        var panel = new DockPanel();
        DockPanel.SetDock(_limitInfo, Dock.Right);
        DockPanel.SetDock(_pageInfo, Dock.Right);
        panel.Children.Add(_limitInfo);
        panel.Children.Add(_pageInfo);
        panel.Children.Add(_statusInfo);

        _statusBar = new Border
        {
            Background = Brushes.Transparent,
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 3),
            Child = panel,
            IsVisible = ShowStatusBar,
        };
    }

    private void UpdateStatus()
    {
        UpdateCounts();
        _pageInfo.Text = string.Format(Loc("PageCountFormat"), Editor.GetPrintPageCount());
        if (!string.IsNullOrEmpty(_limitInfo.Text) && Editor.GetImageCount() <= Editor.MaxRecommendedImages)
            _limitInfo.Text = "";
    }

    private void UpdateCounts()
    {
        if (_statusInfo is null) return;
        var (chars, words, line, col) = Editor.GetStatus();
        _statusInfo.Text = string.Format(Loc("StatusFormat"), chars, words, line, col);
    }

    private void UpdateImageWarning()
        => _limitInfo.Text = string.Format(Loc("ImageLimitWarning"), Editor.GetImageCount(), Editor.MaxRecommendedImages);
}
