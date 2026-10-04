using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace BrainPending;

public partial class MainWindow
{
    private bool _findOpen;

    private void InitializeFind()
    {
        if (MotionSettings.AnimationsEnabled)
            FindPanel.Transitions = new Transitions
            {
                new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(160) }
            };
        EditorView.Editor.SelectionChanged += (_, _) => UpdateFindCount();
        EditorView.Editor.TextChanged += (_, _) => UpdateFindCount();
    }

    private void OpenFind()
    {
        if (_thought == null) return;
        _findOpen = true;
        _searchHighlight = false;
        FindPanel.IsVisible = true;
        FindPanel.Height = 52;
        EditorView.Editor.SetFindHighlight(FindInput.Text, false);
        UpdateFindCount();
        FindInput.Focus();
        FindInput.SelectAll();
    }

    private void CloseFind(bool focusEditor = true)
    {
        _findOpen = false;
        FindPanel.Height = 0;
        FindPanel.IsVisible = false;
        EditorView.Editor.ClearFindHighlight();
        if (focusEditor) EditorView.Editor.Focus();
    }

    private void UpdateFindCount()
    {
        if (!_findOpen) return;
        var (current, total) = EditorView.Editor.GetFindMatchPosition();
        FindCount.Text = total == 0 ? "0 matches" : $"{current} of {total}";
        FindPrevious.IsEnabled = FindNextButton.IsEnabled = total > 0;
    }

    private void FindInput_Changed(object? sender, TextChangedEventArgs e)
    {
        if (!_findOpen) return;
        EditorView.Editor.SetFindHighlight(FindInput.Text, false);
        MoveFind(false);
    }

    private void MoveFind(bool backwards)
    {
        var query = FindInput.Text ?? "";
        if (backwards) EditorView.Editor.FindPrev(query, false);
        else EditorView.Editor.FindNext(query, false);
        UpdateFindCount();
    }

    private bool HandleFindShortcut(KeyEventArgs e)
    {
        if (_thought == null) return false;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) OpenFind();
        else if (e.Key == Key.F3 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            if (!_findOpen) OpenFind();
            MoveFind(e.KeyModifiers == KeyModifiers.Shift);
        }
        else if (_findOpen && e.Key == Key.Escape) CloseFind();
        else if (_findOpen && FindInput.IsFocused && e.Key == Key.Enter &&
                 e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            MoveFind(e.KeyModifiers == KeyModifiers.Shift);
        else return false;
        e.Handled = true;
        return true;
    }

    private void FindPrevious_Click(object? sender, RoutedEventArgs e) => MoveFind(true);
    private void FindNext_Click(object? sender, RoutedEventArgs e) => MoveFind(false);
    private void CloseFind_Click(object? sender, RoutedEventArgs e) => CloseFind();
}
