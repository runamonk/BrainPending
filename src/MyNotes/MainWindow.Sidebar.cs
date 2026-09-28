using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MyNotes;

public partial class MainWindow
{
    private readonly DispatcherTimer _sidebarHide = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _sidebarHideComplete = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _sidebarWidth = 300;
    private const double MiniSidebarWidth = 48;
    private bool _sidebarMenuOpen;
    private bool _pointerInSidebarBounds;
    private bool _sidebarClosedByButton;
    private readonly TranslateTransform _sidebarSlide = new();

    private void InitializeSidebar()
    {
        Sidebar.RenderTransform = _sidebarSlide;
        ApplySidebarLayout();
        MiniSidebar.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (!ReferenceEquals(e.Source, SidebarReveal) && !_settings.SidebarPinned && Sidebar.IsVisible)
                CollapseSidebarFromButton();
        }, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, (_, e) => UpdateSidebarPointer(e), RoutingStrategies.Tunnel);
        PointerExited += (_, _) =>
        {
            _pointerInSidebarBounds = false;
            if (!_settings.SidebarPinned) _sidebarHide.Start();
        };
        _sidebarHide.Tick += (_, _) =>
        {
            if (ShouldKeepSidebarOpen())
                return;
            _sidebarHide.Stop();
            if (!_settings.SidebarPinned) HideSidebar();
        };
        _sidebarHideComplete.Tick += (_, _) =>
        {
            _sidebarHideComplete.Stop();
            if (ShouldKeepSidebarOpen())
            {
                ShowSidebar();
                return;
            }
            Sidebar.IsVisible = false;
        };
        Sidebar.LostFocus += (_, _) => { if (!_settings.SidebarPinned) _sidebarHide.Start(); };
        Closed += (_, _) => { _sidebarHide.Stop(); _sidebarHideComplete.Stop(); };
    }

    private bool ShouldKeepSidebarOpen() => _settings.SidebarPinned || (!_sidebarClosedByButton &&
        (_pointerInSidebarBounds || Sidebar.IsKeyboardFocusWithin || _inDialog || _sidebarMenuOpen));

    private void UpdateSidebarPointer(PointerEventArgs e)
    {
        if (_sidebarClosedByButton)
        {
            var overToggle = new Rect(SidebarReveal.Bounds.Size).Contains(e.GetPosition(SidebarReveal));
            if (overToggle || _sidebarHideComplete.IsEnabled) return;
            _sidebarClosedByButton = false;
        }
        // Use the pane's open bounds, independent of animated transforms and child hit testing.
        var point = e.GetPosition(WorkspaceGrid);
        if (Sidebar.IsVisible)
        {
            var width = _sidebarWidth + (_settings.SidebarPinned ? 0 : MiniSidebarWidth);
            _pointerInSidebarBounds = point.X >= 0 && point.X < width &&
                point.Y >= 0 && point.Y < WorkspaceGrid.Bounds.Height;
        }
        else
        {
            _pointerInSidebarBounds = false;
            return;
        }
        if (_settings.SidebarPinned) return;
        if (_pointerInSidebarBounds)
        {
            if (Sidebar.IsVisible) ShowSidebar();
        }
        else
        {
            if (!_sidebarHide.IsEnabled && !_sidebarHideComplete.IsEnabled) _sidebarHide.Start();
        }
    }

    private void ApplySidebarLayout()
    {
        var pinned = _settings.SidebarPinned;
        _sidebarClosedByButton = false;
        _sidebarHideComplete.Stop();
        _sidebarSlide.Transitions = null;
        _sidebarSlide.X = 0;
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(pinned ? _sidebarWidth : MiniSidebarWidth);
        WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(pinned ? 5 : 0);
        Grid.SetColumnSpan(Sidebar, pinned ? 1 : 3);
        Sidebar.HorizontalAlignment = pinned ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Sidebar.Width = pinned ? double.NaN : _sidebarWidth;
        Sidebar.Margin = new Thickness(pinned ? 0 : MiniSidebarWidth, 0, 0, 0);
        Sidebar.IsVisible = pinned;
        MiniSidebar.IsVisible = !pinned;
        SidebarToolbar.IsVisible = pinned;
        SidebarOverlayPin.IsVisible = !pinned;
        SidebarSplitter.IsVisible = pinned;
        SidebarReveal.IsVisible = !pinned;
        SidebarPinnedFill.IsVisible = pinned;
        var label = pinned ? "Unpin sidebar (auto-hide)" : "Pin sidebar";
        ToolTip.SetTip(SidebarPin, label);
        AutomationProperties.SetName(SidebarPin, label);
        UpdateSidebarToggle(pinned);
    }

    private void ShowSidebar()
    {
        _sidebarClosedByButton = false;
        UpdateSidebarToggle(true);
        _sidebarHide.Stop();
        _sidebarHideComplete.Stop();
        var wasVisible = Sidebar.IsVisible;
        if (!wasVisible && !_settings.SidebarPinned && MotionSettings.AnimationsEnabled)
        {
            _sidebarSlide.Transitions = null;
            // Realize the list in its visible viewport before sliding it in from outside the window.
            _sidebarSlide.X = 0;
            Sidebar.IsVisible = true;
            Sidebar.UpdateLayout();
            _sidebarSlide.X = -_sidebarWidth;
            EnableSidebarSlide();
        }
        else Sidebar.IsVisible = true;
        _sidebarSlide.X = 0;
    }

    private void EnableSidebarSlide()
    {
        _sidebarSlide.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new SineEaseInOut()
            }
        };
    }

    private void HideSidebar()
    {
        if (!Sidebar.IsVisible || _sidebarHideComplete.IsEnabled) return;
        UpdateSidebarToggle(false);
        if (!MotionSettings.AnimationsEnabled)
        {
            Sidebar.IsVisible = false;
            return;
        }
        EnableSidebarSlide();
        _sidebarSlide.X = -_sidebarWidth;
        _sidebarHideComplete.Start();
    }

    private void SidebarPin_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings.SidebarPinned) _sidebarWidth = Math.Max(200, Sidebar.Bounds.Width);
        var pinned = !_settings.SidebarPinned;
        _settings = NotebookSettings.Read(_settingsPath) with { SidebarPinned = pinned };
        try { _settings.Save(_settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowNotice("Could not remember sidebar preference: " + error.Message); }
        ApplySidebarLayout();
        Sidebar.IsVisible = true;
        ShowSidebar();
        if (!pinned)
        {
            // The pin must not retain focus and prevent the pane from hiding.
            SidebarReveal.Focus();
            _sidebarHide.Start();
        }
    }

    private void SidebarReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (Sidebar.IsVisible && !_sidebarHideComplete.IsEnabled)
            CollapseSidebarFromButton();
        else { ShowSidebar(); SearchBox.Focus(); }
    }

    private void CollapseSidebarFromButton()
    {
        _sidebarHide.Stop();
        _sidebarClosedByButton = true;
        // Do not steal focus from an action's dialog or the newly opened note.
        if (Sidebar.IsKeyboardFocusWithin) SidebarReveal.Focus();
        HideSidebar();
    }

    private void UpdateSidebarToggle(bool open)
    {
        var label = open ? "Hide sidebar" : "Show sidebar";
        ToolTip.SetTip(SidebarReveal, label);
        AutomationProperties.SetName(SidebarReveal, label);
    }
    private void Sidebar_PointerEntered(object? sender, PointerEventArgs e) => UpdateSidebarPointer(e);
    private void Sidebar_PointerExited(object? sender, PointerEventArgs e) => UpdateSidebarPointer(e);
}
