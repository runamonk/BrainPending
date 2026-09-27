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
    private readonly DispatcherTimer _sidebarRevealDelay = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _sidebarHide = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _sidebarHideComplete = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _sidebarWidth = 300;
    private bool _sidebarMenuOpen;
    private bool _pointerInSidebarBounds;
    private readonly TranslateTransform _sidebarSlide = new();

    private void InitializeSidebar()
    {
        Sidebar.RenderTransform = _sidebarSlide;
        ApplySidebarLayout();
        _sidebarRevealDelay.Tick += (_, _) =>
        {
            _sidebarRevealDelay.Stop();
            if (!_settings.SidebarPinned && _pointerInSidebarBounds) ShowSidebar();
        };
        AddHandler(PointerMovedEvent, (_, e) => UpdateSidebarPointer(e), RoutingStrategies.Tunnel);
        PointerExited += (_, _) =>
        {
            _pointerInSidebarBounds = false;
            _sidebarRevealDelay.Stop();
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
        Closed += (_, _) => { _sidebarRevealDelay.Stop(); _sidebarHide.Stop(); _sidebarHideComplete.Stop(); };
    }

    private bool ShouldKeepSidebarOpen() => _settings.SidebarPinned || _pointerInSidebarBounds ||
        Sidebar.IsKeyboardFocusWithin || _inDialog || _sidebarMenuOpen;

    private void UpdateSidebarPointer(PointerEventArgs e)
    {
        // Use the pane's open bounds, independent of animated transforms and child hit testing.
        var point = e.GetPosition(WorkspaceGrid);
        var width = Sidebar.IsVisible ? _sidebarWidth : SidebarReveal.Width;
        _pointerInSidebarBounds = point.X >= 0 && point.X < width &&
            point.Y >= 0 && point.Y < WorkspaceGrid.Bounds.Height;
        if (_settings.SidebarPinned) return;
        if (_pointerInSidebarBounds)
        {
            if (Sidebar.IsVisible) ShowSidebar();
            else if (!_sidebarRevealDelay.IsEnabled) _sidebarRevealDelay.Start();
        }
        else
        {
            _sidebarRevealDelay.Stop();
            if (!_sidebarHide.IsEnabled && !_sidebarHideComplete.IsEnabled) _sidebarHide.Start();
        }
    }

    private void ApplySidebarLayout()
    {
        var pinned = _settings.SidebarPinned;
        _sidebarRevealDelay.Stop();
        _sidebarHideComplete.Stop();
        _sidebarSlide.Transitions = null;
        _sidebarSlide.X = 0;
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(pinned ? _sidebarWidth : 0);
        WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(pinned ? 5 : 0);
        Grid.SetColumnSpan(Sidebar, pinned ? 1 : 3);
        Sidebar.HorizontalAlignment = pinned ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Sidebar.Width = pinned ? double.NaN : _sidebarWidth;
        Sidebar.IsVisible = pinned;
        SidebarSplitter.IsVisible = pinned;
        SidebarReveal.IsVisible = !pinned;
        SidebarPinnedFill.IsVisible = pinned;
        var label = pinned ? "Unpin sidebar (auto-hide)" : "Pin sidebar";
        ToolTip.SetTip(SidebarPin, label);
        AutomationProperties.SetName(SidebarPin, label);
    }

    private void ShowSidebar()
    {
        _sidebarRevealDelay.Stop();
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

    private void SidebarReveal_PointerEntered(object? sender, PointerEventArgs e) => UpdateSidebarPointer(e);
    private void SidebarReveal_Click(object? sender, RoutedEventArgs e) { ShowSidebar(); SearchBox.Focus(); }
    private void Sidebar_PointerEntered(object? sender, PointerEventArgs e) => UpdateSidebarPointer(e);
    private void Sidebar_PointerExited(object? sender, PointerEventArgs e) => UpdateSidebarPointer(e);
}
