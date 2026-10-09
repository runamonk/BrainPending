using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaRichEditor.Documents;

namespace AvaloniaRichEditor.Controls;

/// <summary>Formatting toolbar driven by Target. Buttons retain editor focus; picker popups restore it on close.</summary>
public partial class RichEditorToolbar : UserControl
{
    /// <summary>Show common text, list, and insert controls inline without an overflow menu.</summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<RichEditorToolbar, bool>(nameof(Compact));
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <inheritdoc cref="Target"/>
    public static readonly StyledProperty<RichEditor?> TargetProperty =
        AvaloniaProperty.Register<RichEditorToolbar, RichEditor?>(nameof(Target));

    /// <summary>The editor this toolbar drives. The toolbar is hidden while this is null.</summary>
    public RichEditor? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Host controls shown at the start of the strip, before the formatting buttons (e.g.
    /// app-shell actions like save/open). Add/remove controls and the toolbar rebuilds. They share the
    /// strip's wrapping, so the whole toolbar stays a single row that wraps together when narrow.</summary>
    public AvaloniaList<Control> LeadingItems { get; } = new();

    /// <summary>Host controls shown at the end of the strip, after the formatting buttons (e.g. zoom).</summary>
    public AvaloniaList<Control> TrailingItems { get; } = new();

    private EventHandler? _attachFileRequested;

    /// <summary>Raised when Attach file is clicked. The button is shown when a host handles this event.</summary>
    public event EventHandler? AttachFileRequested
    {
        add { _attachFileRequested += value; Build(); Sync(); }
        remove { _attachFileRequested -= value; Build(); Sync(); }
    }

    private Button BuildAttachmentButton()
    {
        var paperclip = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 21,11.5 L 12.5,20 A 6,6 0 0 1 4,11.5 L 12.5,3 A 4,4 0 0 1 18.2,8.7 L 9.7,17.2 A 2,2 0 0 1 6.8,14.3 L 14.6,6.5"),
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        paperclip.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(paperclip);
        var button = new Button
        {
            Name = "AttachFileButton",
            Content = new Viewbox { Width = 20, Height = 20, Child = canvas, Stretch = Stretch.Uniform },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3),
            Focusable = false
        };
        Avalonia.Automation.AutomationProperties.SetName(button, "Attach file");
        ToolTip.SetTip(button, "Attach file");
        button.Click += (_, _) => _attachFileRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    // Shared brushes must be immutable so toolbars on different UI threads can use them.
    private static readonly IBrush ActiveBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#506780"));

    private Button? _boldBtn, _italicBtn, _underlineBtn, _strikeBtn, _bulletBtn, _numberBtn, _undoBtn, _redoBtn, _wrapBtn;
    private ComboBox? _fontCombo, _sizeCombo, _headingCombo, _alignCombo;
    private TextBox? _spacingBox;
    private TextBlock? _bulletPreview, _numberPreview;
    private static readonly int[] SpacingPercents = { 100, 110, 120, 130, 150, 160, 180, 200, 250, 300 };

    private static double[] _fontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 };

    /// <summary>The font sizes (in points) offered by the toolbar's size combo. Hosts can replace this
    /// array to customize the options.
    /// <para>Read while the strip is being built, so assign it BEFORE creating the toolbar. An existing
    /// toolbar keeps the sizes it was built with until something rebuilds it.</para></summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">The array is empty, or holds a size that is not a positive
    /// finite number.</exception>
    public static double[] FontSizes
    {
        get => _fontSizes;
        // Validate before building controls so invalid sizes fail at the assignment that supplied them.
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
                throw new ArgumentException("At least one font size is required.", nameof(value));
            foreach (double pt in value)
                if (!double.IsFinite(pt) || pt <= 0)
                    throw new ArgumentException($"Font size must be a positive finite number, was {pt}.", nameof(value));
            _fontSizes = value;
        }
    }

    // Use identical invariant labels for items and caret reflection; fractional sizes must round-trip.
    private static string SizeText(double pt)
        => pt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    private Control? _tableBtn, _imageBtn, _attachmentBtn, _dividerBtn;
    // Color-picker faces, synced to the caret's run: either a swatch bar under the built-in glyph,
    // or (with a host icon) a wrapper whose Foreground tints the icon's colour-inheriting layers.
    private Border? _colorSwatch, _highlightSwatch;
    private ContentControl? _colorIconHost, _highlightIconHost;

    // Immutable for the same reason as ActiveBrush — see the note there.
    private static readonly IBrush NoColorBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#DDDDDD"));
    private static readonly IBrush DimInk = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#BFC3C7")); // inactive list marker

    private void ReflectPickerColor(bool highlight, IBrush brush)
    {
        if (highlight)
        {
            if (_highlightSwatch != null) _highlightSwatch.Background = brush;
            if (_highlightIconHost != null) _highlightIconHost.Foreground = brush;
        }
        else
        {
            if (_colorSwatch != null) _colorSwatch.Background = brush;
            if (_colorIconHost != null) _colorIconHost.Foreground = brush;
        }
    }
    private bool _suppress; // guards combo SelectionChanged while syncing toolbar -> caret state

    private static string Loc(string key) => RichEditorLocalization.GetString(key);

    public RichEditorToolbar()
    {
        // Disabled buttons (undo/redo) dim via opacity instead of the theme's grey fill, which
        // reads as a stray box on this flat transparent strip.
        Styles.Add(new Style(x => x.OfType<Button>().Class(":disabled"))
        {
            Setters = { new Setter(OpacityProperty, 0.35) },
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class(":disabled").Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
        });
        LeadingItems.CollectionChanged += (_, _) => { Build(); Sync(); };
        TrailingItems.CollectionChanged += (_, _) => { Build(); Sync(); };
        Build();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RichEditorLocalization.LanguageChanged += OnLanguageChanged;
        Sync();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        RichEditorLocalization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // LanguageChanged can arrive on a worker thread; rebuilding Avalonia controls requires the UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e));
            return;
        }
        Build();
        Sync();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty) { Build(); Sync(); }
        if (change.Property == TargetProperty)
        {
            if (change.OldValue is RichEditor old)
            {
                old.StatusChanged -= OnTargetStatusChanged;
                old.SelectionChanged -= OnTargetStatusChanged;
                old.PropertyChanged -= OnTargetPropertyChanged;
            }
            if (change.NewValue is RichEditor rt)
            {
                rt.StatusChanged += OnTargetStatusChanged;
                rt.SelectionChanged += OnTargetStatusChanged;
                rt.PropertyChanged += OnTargetPropertyChanged;
            }
            Build(); // font list comes from the target, so rebuild
            Sync();
        }
    }

    private void OnTargetStatusChanged(object? sender, EventArgs e) => Sync();

    private void OnTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RichEditor.FontFamilyChoicesProperty) { Build(); Sync(); }
        else if (e.Property == RichEditor.IsReadOnlyProperty) { Build(); Sync(); } // editable vs view toolbar differ structurally
        else if (e.Property == RichEditor.WordWrapProperty) Sync(); // also toggled from the right-click menu
        else if (e.Property == RichEditor.AllowImagesProperty
              || e.Property == RichEditor.AllowTablesProperty) ApplyFlags();
        else if (e.Property == RichEditor.PageSizeProperty
              || e.Property == RichEditor.PageOrientationProperty
              || e.Property == RichEditor.ShowPageBoundariesProperty) SyncPage();
    }

    // Pickers take focus while open; restore it so typing resumes in the editor after dismissal.
    private Flyout PickerFlyout(Flyout f)
    {
        f.Closed += (_, _) => Target?.Focus();
        return f;
    }

    // Clears Focusable on every Button in a built subtree. Host-supplied Leading/TrailingItems are walked
    // too: they sit in the same strip, so a focus grab there hides the caret just the same.
    private static void DisableButtonFocus(Control c)
    {
        if (c is Button b) b.Focusable = false;
        foreach (var child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(c))
            if (child is Control cc) DisableButtonFocus(cc);
    }


    private void Build()
    {
        var items = new System.Collections.Generic.List<Control>();
        Control? colorButton = null, highlightButton = null, linkButton = null;
        void Add(Control c) => items.Add(c);

        Button Btn(object content, string tip, Action click, RichEditorIcon? icon = null)
        {
            var resolved = (icon is { } k ? RichEditorIcons.TryCreate(k) : null)
                          ?? (icon is { } vk ? ToolbarIcons.Create(vk) : null);
            var b = new Button
            {
                Content = resolved ?? content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                // Icon buttons get tighter padding so the larger (20px) glyph keeps the same button size
                // the letter buttons (B/I/U/S) have with their original padding.
                Padding = resolved != null ? new Thickness(7, 3) : new Thickness(9, 5),
                Margin = new Thickness(1, 0),
                MinWidth = 30,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { click(); Sync(); };
            return b;
        }
        ComboBox Combo(string tip, double minWidth = 0)
        {
            var cb = new ComboBox
            {
                Margin = new Thickness(2, 0),
                MinHeight = 28,
                MinWidth = minWidth,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                [!ComboBox.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
                BorderThickness = Compact ? new Thickness(0) : new Thickness(1),
                Background = Brushes.Transparent,
                Padding = Compact ? new Thickness(6, 2) : new Thickness(8, 4),
            };
            ToolTip.SetTip(cb, tip);
            // A combo legitimately needs focus while its list is open, so it can't simply refuse it like
            // the buttons do. Hand focus back once the dropdown closes — doing it on SelectionChanged
            // instead would yank focus away while arrowing through an open list.
            cb.DropDownClosed += (_, _) => Target?.Focus();
            return cb;
        }
        Control Div() => new Border
        {
            Width = 1,
            Height = 22,
            Margin = new Thickness(6, 4),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _undoBtn = _redoBtn = _boldBtn = _italicBtn = _underlineBtn = _strikeBtn = _bulletBtn = _numberBtn = _wrapBtn = null;
        _fontCombo = _sizeCombo = _headingCombo = _alignCombo = null;
        _spacingBox = null; _bulletPreview = _numberPreview = null;
        _tableBtn = _imageBtn = _attachmentBtn = _dividerBtn = null;
        _zoomCombo = _paperCombo = _orientCombo = null; _exportBtn = _importBtn = _printBtn = null;
        _colorSwatch = _highlightSwatch = null; _colorIconHost = _highlightIconHost = null;

        Control? bulletBox = null, numberBox = null;
        bool ro = Target?.IsReadOnly == true;
        var lvl = EffectiveLevel();

        if (ro)
        {
            if (ShowPageControls) BuildPageControls(items);
            if (ShowFileActions) { if (items.Count > 0) Add(Div()); BuildFileActions(items); }
        }
        else
        {
            _boldBtn = Btn(new TextBlock { Text = "B", FontWeight = FontWeight.Bold }, Loc("Bold") + " (Ctrl+B)", () => Target?.ToggleBold(), RichEditorIcon.Bold);
            _italicBtn = Btn(new TextBlock { Text = "I", FontStyle = FontStyle.Italic }, Loc("Italic") + " (Ctrl+I)", () => Target?.ToggleItalic(), RichEditorIcon.Italic);
            _underlineBtn = Btn(new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, Loc("Underline") + " (Ctrl+U)", () => Target?.ToggleUnderline(), RichEditorIcon.Underline);
            _strikeBtn = Btn(new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, Loc("Strikethrough"), () => Target?.ToggleStrikethrough(), RichEditorIcon.Strikethrough);
            Add(_boldBtn); Add(_italicBtn); Add(_underlineBtn); Add(_strikeBtn);
            Add(Div());

            Add(colorButton = BuildColorButton(highlight: false));
            Add(highlightButton = BuildColorButton(highlight: true));
            Add(Div());

            _fontCombo = Combo(Loc("FontFamily"), 120);
            // The closed selection may supply null. Disable recycling because FontFamily is set at build time.
            _fontCombo.ItemTemplate = new FuncDataTemplate<string>(
                (name, _) => new TextBlock
                {
                    Text = name,
                    FontFamily = string.IsNullOrEmpty(name) ? FontFamily.Default : new FontFamily(name)
                });
            foreach (var f in Target?.FontFamilyChoices ?? Array.Empty<string>())
                _fontCombo.Items.Add(f);
            _fontCombo.SelectionChanged += (_, _) =>
            {
                if (_suppress || _fontCombo.SelectedItem is not string fam) return;
                Target?.SetFontFamily(fam);
                Sync();
            };
            Add(_fontCombo);
            

            _sizeCombo = Combo(Loc("FontSize"), 60);
            foreach (var s in FontSizes)
                _sizeCombo.Items.Add(new ComboBoxItem { Content = SizeText(s) });
            _sizeCombo.SelectionChanged += (_, _) =>
            {
                if (_suppress || _sizeCombo.SelectedItem is not ComboBoxItem it) return;
                if (double.TryParse(it.Content?.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double size))
                {
                    Target?.SetFontSize(size);
                    Sync();
                }
            };
            Add(_sizeCombo);
            Add(_wrapBtn = Btn("↵", Loc("WordWrap"), () => { if (Target is { } t) t.WordWrap = !t.WordWrap; }, RichEditorIcon.WordWrap));
            Add(Div());

            var bullet = BuildListBox(RichEditorIcon.BulletList, Loc("BulletList"), () => Target?.ToggleBullet(), ListKind.Bullet,
                (ListMarkerStyle.Disc, "•"), (ListMarkerStyle.Circle, "◦"),
                (ListMarkerStyle.Square, "▪"), (ListMarkerStyle.Dash, "–"));
            _bulletBtn = bullet.Icon; _bulletPreview = bullet.Preview;
            Add(bulletBox = bullet.Box);
            var number = BuildListBox(RichEditorIcon.NumberedList, Loc("NumberedList"), () => Target?.ToggleNumbering(), ListKind.Ordered,
                (ListMarkerStyle.Decimal, "1."), (ListMarkerStyle.DecimalParen, "1)"),
                (ListMarkerStyle.LowerAlpha, "a)"), (ListMarkerStyle.UpperAlpha, "A)"),
                (ListMarkerStyle.LowerRoman, "i)"));
            _numberBtn = number.Icon; _numberPreview = number.Preview;
            Add(numberBox = number.Box);
            Add(Div());

            _tableBtn = BuildTableButton();
            _imageBtn = Btn("🖼", Loc("InsertImage"), () => { _ = Target?.InsertImageFromFileAsync(); }, RichEditorIcon.InsertImage);
            _dividerBtn = Btn("―", Loc("InsertDivider"), () => Target?.InsertDivider(), RichEditorIcon.InsertDivider);
            Add(_tableBtn); Add(_imageBtn);
            if (_attachFileRequested != null) Add(_attachmentBtn = BuildAttachmentButton());
            Add(_dividerBtn);
            Add(linkButton = Btn("Link", "Insert or edit hyperlink", () => { _ = Target?.EditLinkFromToolbarAsync(); }, RichEditorIcon.InsertLink));           
            Add(Btn("Clear", "Clear formatting", () => Target?.ClearFormatting()));

        }

        if (Compact && !ro)
        {
            var primary = new System.Collections.Generic.List<Control>();
            foreach (var control in new Control?[] { _boldBtn, _italicBtn, _underlineBtn, _strikeBtn,
                colorButton, highlightButton, _fontCombo, _sizeCombo, _wrapBtn, linkButton, _imageBtn, _attachmentBtn, bulletBox, numberBox, _tableBtn })
                if (control != null) primary.Add(control);
            items = primary;
        }

        // WrapPanel avoids changing the visual tree during layout, which can cause resize re-entrancy.
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        // Host items detach from the previous build's panel before re-adding (a control has one parent).
        void AddHost(Control c) { (c.Parent as Panel)?.Children.Remove(c); wrap.Children.Add(c); }
        foreach (var c in LeadingItems) AddHost(c);
        if (LeadingItems.Count > 0) wrap.Children.Add(Div());
        foreach (var c in items) wrap.Children.Add(c);
        // Include host-supplied buttons: any button taking focus would hide the caret and interrupt typing.
        foreach (var c in wrap.Children) DisableButtonFocus(c);
        if (TrailingItems.Count > 0) wrap.Children.Add(Div());
        foreach (var c in TrailingItems) AddHost(c);

        Content = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4),
            Child = wrap,
        };
        ApplyFlags();
    }

    /// <summary>The colour palette (hex strings) offered by the toolbar's text and highlight pickers.
    /// Hosts can replace this array. The default is 40 swatches: greys plus hues in a few shades.
    /// <para>Read when a colour flyout is built, so assign it before the toolbar is created. Entries are
    /// parsed by <c>Color.Parse</c>; an entry that does not parse renders as BLACK rather than throwing,
    /// so a typo shows up as an unexpected swatch, not a toolbar that fails to build.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">The array is empty.</exception>
    public static string[] Palette
    {
        get => _palette;
        // Invalid swatch text falls back to black; only null/empty palettes prevent a usable picker.
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
                throw new ArgumentException("At least one palette entry is required.", nameof(value));
            _palette = value;
        }
    }

    // A malformed host-supplied swatch must not abort the toolbar build.
    private static Color ParseSwatch(string hex)
    {
        try { return Color.Parse(hex); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return Colors.Black; }
    }

    private static string[] _palette =
    {
        "#000000","#444444","#666666","#999999","#BBBBBB","#DDDDDD","#EEEEEE","#FFFFFF",
        "#FF0000","#E67E22","#F1C40F","#2ECC71","#1ABC9C","#3498DB","#9B59B6","#E91E63",
        "#C0392B","#D35400","#F39C12","#27AE60","#16A085","#2980B9","#8E44AD","#AD1457",
        "#7B241C","#935116","#9A7D0A","#196F3D","#0E6251","#1A5276","#5B2C6F","#78281F",
        "#FFCDD2","#FFE0B2","#FFF9C4","#C8E6C9","#B2DFDB","#BBDEFB","#E1BEE7","#F8BBD0",
    };

    private Button BuildColorButton(bool highlight)
    {
        // Host icons inherit the current color through Foreground; built-in icons use a separate swatch bar.
        var initial = new SolidColorBrush(highlight ? Color.Parse("#FFF176") : Colors.Black);
        Control face;
        if (RichEditorIcons.TryCreate(highlight ? RichEditorIcon.Highlight : RichEditorIcon.TextColor) is { } icon)
        {
            var host = new ContentControl { Content = icon, Foreground = initial };
            if (highlight) { _highlightIconHost = host; _highlightSwatch = null; }
            else { _colorIconHost = host; _colorSwatch = null; }
            face = host;
        }
        else
        {
            var swatch = new Border
            {
                Height = Compact ? 2 : 6,
                MinWidth = Compact ? 16 : 24,
                CornerRadius = new CornerRadius(1),
                Background = initial,
                Margin = new Thickness(0, 2, 0, 0),
            };
            if (highlight) { _highlightSwatch = swatch; _highlightIconHost = null; }
            else { _colorSwatch = swatch; _colorIconHost = null; }
            Control glyph = highlight
                ? (ToolbarIcons.Create(RichEditorIcon.Highlight) ?? (Control)new TextBlock { Text = "🖍", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center })
                : new TextBlock { Text = "A", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel();
            stack.Children.Add(glyph);
            stack.Children.Add(swatch);
            face = stack;
        }

        var btn = new Button
        {
            Content = face,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            // The stacked glyph+swatch face is taller, so it gets less vertical padding; an
            // icon-only face uses the same padding as the other toolbar buttons.
            Padding = face is StackPanel ? new Thickness(9, 3) : new Thickness(9, 5),
            Margin = new Thickness(1, 0),
            MinWidth = 30,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(btn, Loc(highlight ? "Highlight" : "TextColor"));

        void Apply(Color? c)
        {
            if (Target == null) return;
            IBrush? brush = c.HasValue ? new SolidColorBrush(c.Value) : null;
            if (highlight) Target.SetHighlight(brush);
            else Target.SetForeground(brush ?? Brushes.Black);
            ReflectPickerColor(highlight, c.HasValue ? new SolidColorBrush(c.Value) : NoColorBrush);
            (btn.Flyout as FlyoutBase)?.Hide();
        }

        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in Palette)
        {
            var color = ParseSwatch(hex);
            var sw = new Button
            {
                Background = new SolidColorBrush(color),
                Width = 22,
                Height = 22,
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
                BorderThickness = new Thickness(1),
                Focusable = false, // see the Btn factory — the caret must survive a swatch click
            };
            sw.Click += (_, _) => Apply(color);
            grid.Children.Add(sw);
        }

        var panel = new StackPanel { Spacing = 6, Width = 200 };
        panel.Children.Add(grid);

        if (highlight)
        {
            var none = new Button { Content = Loc("NoHighlight"), HorizontalAlignment = HorizontalAlignment.Stretch };
            none.Click += (_, _) => Apply(null);
            panel.Children.Add(none);
        }

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var hexBox = new TextBox { PlaceholderText = "#RRGGBB", Width = 110 };
        var applyBtn = new Button { Content = Loc("Apply") };
        applyBtn.Click += (_, _) => { if (Color.TryParse(hexBox.Text, out var c)) Apply(c); };
        hexRow.Children.Add(hexBox);
        hexRow.Children.Add(applyBtn);
        panel.Children.Add(hexRow);

        btn.Flyout = PickerFlyout(new Flyout { Content = panel });
        return btn;
    }

    private Button BuildTableButton()
    {
        object tableFace = "▦ ▾";
        var tableGlyph = RichEditorIcons.TryCreate(RichEditorIcon.InsertTable) ?? ToolbarIcons.Create(RichEditorIcon.InsertTable);
        if (tableGlyph != null)
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(tableGlyph);
            face.Children.Add(ToolbarIcons.ChevronDown());
            tableFace = face;
        }
        var btn = new Button
        {
            Content = tableFace,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 5),
            Margin = new Thickness(1, 0),
            MinWidth = 30,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(btn, Loc("InsertTable"));

        const int rows = 8, cols = 10;
        var cells = new Border[rows, cols];
        var grid = new UniformGrid { Columns = cols, Rows = rows };
        var label = new TextBlock { Text = Loc("DragToSelectSize"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };

        void Highlight(int hr, int hc)
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    cells[r, c].Background = (r <= hr && c <= hc) ? ActiveBrush : Brushes.White;
            label.Text = $"{hr + 1} × {hc + 1}";
        }

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int rr = r, cc = c;
                var cell = new Border
                {
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(1),
                    [!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush"),
                    [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
                    BorderThickness = new Thickness(1),
                };
                cell.PointerEntered += (_, _) => Highlight(rr, cc);
                cell.PointerPressed += (_, _) =>
                {
                    // Arm "draw table" mode: the chosen rows×cols is drawn at the size of the next drag on
                    // the editor (a plain click falls back to the default size). Same as the context menu.
                    Target?.BeginTableDraw(rr + 1, cc + 1);
                    (btn.Flyout as FlyoutBase)?.Hide();
                };
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(grid);
        panel.Children.Add(label);
        btn.Flyout = PickerFlyout(new Flyout { Content = panel });
        return btn;
    }

    // Line-spacing control: one bordered box (matching the combos) holding the glyph, an editable % box,
    // tight ▲▼ steppers and a ▾ presets dropdown. Each maps to Paragraph.LineSpacing = %/100.
    private Control BuildLineSpacingControl()
    {
        var ink = new SolidColorBrush(Color.Parse("#80868B"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        var glyph = RichEditorIcons.TryCreate(RichEditorIcon.LineSpacing) ?? ToolbarIcons.Create(RichEditorIcon.LineSpacing);
        if (glyph != null) { if (glyph is Layoutable lg) lg.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(glyph); }

        _spacingBox = new TextBox
        {
            Text = "100%",
            MinWidth = 0,                            // hug the content (no fixed width => no inner gap)
            FontSize = 12,
            MinHeight = 0,
            Padding = new Thickness(0, 1),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Commit() => ApplySpacingPercent(CurrentSpacingPercent());
        _spacingBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
        _spacingBox.LostFocus += (_, _) => Commit();
        ToolTip.SetTip(_spacingBox, Loc("LineSpacing"));
        row.Children.Add(_spacingBox);

        Button Step(bool up, int delta)
        {
            var chevron = new Avalonia.Controls.Shapes.Path
            {
                Data = Avalonia.Media.Geometry.Parse(up ? "M0 3 L3 0 L6 3" : "M0 0 L3 3 L6 0"),
                Stroke = ink,
                StrokeThickness = 1.2,
                StrokeLineCap = Avalonia.Media.PenLineCap.Round,
                StrokeJoin = Avalonia.Media.PenLineJoin.Round,
                Width = 6,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Button
            {
                Content = chevron,
                Width = 15,
                Height = 8,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            b.Click += (_, _) => ApplySpacingPercent(CurrentSpacingPercent() + delta);
            return b;
        }
        var steppers = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        steppers.Children.Add(Step(true, +10));
        steppers.Children.Add(Step(false, -10));

        var presets = new Button
        {
            Content = ToolbarIcons.ChevronDown(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0),
            MinWidth = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(presets, Loc("LineSpacing"));
        var flyout = PickerFlyout(new Flyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft });
        var panel = new StackPanel { MinWidth = 64 };
        foreach (var pct in SpacingPercents)
        {
            int p = pct;
            var item = new Button
            {
                Content = p + "%",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            item.Click += (_, _) => { ApplySpacingPercent(p); flyout.Hide(); };
            panel.Children.Add(item);
        }
        flyout.Content = panel;
        row.Children.Add(presets);
        row.Children.Add(steppers);

        var box = new Border
        {
            Child = row,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0),
            Margin = new Thickness(2, 0),
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Open the preset menu below the whole box (like a ComboBox), not anchored to the small ▾ button.
        Avalonia.Controls.Primitives.FlyoutBase.SetAttachedFlyout(box, flyout);
        presets.Click += (_, _) => Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(box);
        return box;
    }

    private int CurrentSpacingPercent()
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in _spacingBox?.Text ?? "") if (char.IsDigit(c)) sb.Append(c);
        return int.TryParse(sb.ToString(), out int p) && p > 0 ? p : 100;
    }

    private void ApplySpacingPercent(int pct)
    {
        pct = System.Math.Clamp(pct, 100, 1000);
        if (_spacingBox != null) _spacingBox.Text = pct + "%";
        Target?.SetLineSpacing(pct / 100.0);
    }

    private (Control Box, Button Icon, TextBlock Preview) BuildListBox(
        RichEditorIcon iconKind, string tip, Action toggle, ListKind kind,
        params (ListMarkerStyle Style, string Glyph)[] options)
    {
        var glyph = RichEditorIcons.TryCreate(iconKind) ?? ToolbarIcons.Create(iconKind);
        var icon = new Button
        {
            Content = glyph ?? (object)options[0].Glyph,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            MinWidth = 28,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        icon.Click += (_, _) => toggle();
        ToolTip.SetTip(icon, tip);

        var preview = new TextBlock
        {
            Text = options[0].Glyph,
            FontSize = 12,
            MinWidth = 16,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var presetContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        presetContent.Children.Add(preview);
        presetContent.Children.Add(ToolbarIcons.ChevronDown());
        var presets = new Button
        {
            Content = presetContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 0),
            MinWidth = 40,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTip.SetTip(presets, tip);
        var flyout = PickerFlyout(new Flyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft });
        var panel = new StackPanel { MinWidth = 64 };
        foreach (var (style, g) in options)
        {
            var s = style;
            var item = new Button
            {
                Content = g,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 14,
            };
            item.Click += (_, _) => { Target?.SetListStyle(s); flyout.Hide(); };
            panel.Children.Add(item);
        }
        flyout.Content = panel;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Stretch };
        row.Children.Add(icon); row.Children.Add(presets);
        var box = new Border
        {
            Child = row,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0),
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Open the menu below the whole box (like a ComboBox), not anchored to the small ▾ button.
        Avalonia.Controls.Primitives.FlyoutBase.SetAttachedFlyout(box, flyout);
        presets.Click += (_, _) => Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(box);
        return (box, icon, preview);
    }


    private void ApplyFlags()
    {
        IsVisible = Target != null;
        if (Target == null) return;
        if (_tableBtn != null) _tableBtn.IsVisible = Target.AllowTables;
        if (_imageBtn != null) _imageBtn.IsVisible = Target.AllowImages;
        if (_dividerBtn != null) _dividerBtn.IsVisible = Target.AllowTables || Target.AllowImages;
    }

    private void Sync()
    {
        var rt = Target;
        if (rt == null) return;
        var f = rt.GetCaretFormat();
        var selectionFont = rt.GetSelectionFont(f);

        static void SetActive(Button? b, bool active)
        {
            if (b == null) return;
            if (active) b.Background = ActiveBrush;
            else b.Background = Brushes.Transparent;
        }
        SetActive(_boldBtn, f.Bold);
        SetActive(_italicBtn, f.Italic);
        SetActive(_underlineBtn, f.Underline);
        SetActive(_strikeBtn, f.Strike);
        SetActive(_bulletBtn, f.List == ListKind.Bullet);
        SetActive(_numberBtn, f.List == ListKind.Ordered);
        SetActive(_wrapBtn, rt.WordWrap);
        if (_wrapBtn != null) _wrapBtn.IsVisible = !rt.IsPaged; // page view always wraps
        if (_bulletPreview != null)
        {
            bool on = f.List == ListKind.Bullet;
            _bulletPreview.Text = RichEditor.ListMarkerText(ListKind.Bullet, on ? f.ListMarker : ListMarkerStyle.Default, 1);
            _bulletPreview.Foreground = on ? Foreground : DimInk;
        }
        if (_numberPreview != null)
        {
            bool on = f.List == ListKind.Ordered;
            _numberPreview.Text = RichEditor.ListMarkerText(ListKind.Ordered, on ? f.ListMarker : ListMarkerStyle.Default, 1);
            _numberPreview.Foreground = on ? Foreground : DimInk;
        }
        if (_undoBtn != null) _undoBtn.IsEnabled = rt.CanUndo;
        if (_redoBtn != null) _redoBtn.IsEnabled = rt.CanRedo;
        // Spacing box shows the caret paragraph's current % (unset / ≤1.0 = single = 100%). Set the text
        // directly (not via ApplySpacingPercent) so reflecting the caret doesn't re-apply to the document.
        if (_spacingBox != null && !_spacingBox.IsFocused)
        {
            double ls = f.LineSpacing;
            int pct = double.IsNaN(ls) || ls <= 0 ? 100 : (int)System.Math.Round(ls * 100);
            _spacingBox.Text = pct + "%";
        }

        ReflectPickerColor(highlight: false, f.Foreground ?? Brushes.Black);
        ReflectPickerColor(highlight: true, f.Background ?? NoColorBrush);

        _suppress = true;
        if (_sizeCombo != null)
        {
            _sizeCombo.SelectedItem = null;
            if (selectionFont.Size is { } size) SelectByContent(_sizeCombo, SizeText(size));
        }
        if (_fontCombo != null)
        {
            // Show the common font, or leave the dropdown blank for mixed fonts.
            if (selectionFont.Family == null)
            {
                _fontCombo.SelectedItem = null;
                _fontCombo.PlaceholderText = "";
                _fontCombo.ClearValue(TemplatedControl.FontFamilyProperty);
            }
            else if (_fontCombo.Items.Contains(selectionFont.Family))
            {
                _fontCombo.SelectedItem = selectionFont.Family;
                _fontCombo.PlaceholderText = "";
                // Selected items render through the item template's own font; drop any placeholder font.
                _fontCombo.ClearValue(TemplatedControl.FontFamilyProperty);
            }
            else
            {
                // Fonts outside the list are shown as placeholder text.
                _fontCombo.SelectedItem = null;
                string eff = selectionFont.Family == FontFamily.DefaultFontFamilyName
                    ? EffectiveDefaultFamilyName(rt) : selectionFont.Family;
                _fontCombo.PlaceholderText = eff;
                _fontCombo.FontFamily = string.IsNullOrEmpty(eff) ? FontFamily.Default : new FontFamily(eff);
            }
        }
        if (_headingCombo != null) _headingCombo.SelectedIndex = Math.Min(f.Heading, 6);
        if (_alignCombo != null) _alignCombo.SelectedIndex = f.Align switch
        {
            TextAlignment.Center => 1,
            TextAlignment.Right => 2,
            TextAlignment.Justify => 3,
            _ => 0,
        };
        SyncPage();
        SyncFileActions();
        _suppress = false;
    }

    // Display name of the font a run without explicit FontFamily actually renders with: the
    // editor's DefaultFontFamily, resolving Avalonia's "$Default" sentinel through the FontManager
    // (e.g. "Inter" when the app uses WithInterFont()).
    private static string EffectiveDefaultFamilyName(RichEditor rt)
    {
        var fam = rt.DefaultFontFamily;
        if (fam.Name != FontFamily.DefaultFontFamilyName) return fam.Name;
        return FontManager.Current.DefaultFontFamily.Name;
    }

    private static void SelectByContent(ComboBox cb, string content)
    {
        foreach (var it in cb.Items)
            if (it is ComboBoxItem ci && ci.Content?.ToString() == content) { cb.SelectedItem = ci; return; }
    }
}
