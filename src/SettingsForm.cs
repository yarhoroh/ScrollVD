namespace ScrollVD;

internal sealed class SettingsForm : Form
{
    private readonly MinimapForm? _minimap;

    // --- Panning ---
    private readonly CheckBox _enabled = new() { Text = "Panning enabled", AutoSize = true };
    private readonly CheckBox _edge = new() { Text = "Edge scrolling", AutoSize = true };
    private readonly CheckBox _grab = new() { Text = "Grab with hotkey", AutoSize = true };
    private readonly CheckBox _reverse = new() { Text = "Reverse grab direction", AutoSize = true };
    private readonly CheckBox _snap = new() { Text = "Grid mode — jump by a full screen (9 cells)", AutoSize = true };
    private readonly CheckBox _edgeShift = new() { Text = "Edge jump only while Shift is held", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    private readonly ComboBox _hotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };

    private readonly NumericUpDown _baseSpeed = Num(1, 200);
    private readonly NumericUpDown _maxSpeed = Num(1, 400);
    private readonly NumericUpDown _accel = Num(0, 500);
    private readonly NumericUpDown _dwell = Num(0, 2000);
    private readonly NumericUpDown _corner = Num(0, 400);
    private readonly NumericUpDown _margin = Num(1, 50);
    private readonly ComboBox _layout = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };

    // --- Minimap ---
    private readonly CheckBox _mmVisible = new() { Text = "Show minimap", AutoSize = true };
    private readonly ComboBox _mmHotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _mmWidth = Num(80, 800);

    private readonly TableLayoutPanel _grid = new()
    {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Padding = new Padding(0, 0, 0, 8),
    };
    private int _row;

    private static readonly (HotkeyCombo combo, string label)[] Hotkeys =
    {
        (HotkeyCombo.CtrlShift, "Ctrl + Shift"),
        (HotkeyCombo.CtrlAlt,   "Ctrl + Alt"),
        (HotkeyCombo.ShiftAlt,  "Shift + Alt"),
        (HotkeyCombo.WinShift,  "Win + Shift"),
        (HotkeyCombo.CtrlWin,   "Ctrl + Win"),
    };

    // Grid presets: cols × rows with the home (main) cell at (homeCol, homeRow).
    private static readonly (string label, int cols, int rows, int homeCol, int homeRow)[] Layouts =
    {
        ("1 × 2  — main bottom, extra above", 1, 2, 0, 1),
        ("1 × 2  — main top, extra below",    1, 2, 0, 0),
        ("2 × 1  — main left, extra right",   2, 1, 0, 0),
        ("2 × 1  — main right, extra left",   2, 1, 1, 0),
        ("2 × 2  (4 cells)",                  2, 2, 0, 1),
        ("3 × 2  (6 cells)",                  3, 2, 0, 1),
        ("2 × 3  (6 cells)",                  2, 3, 0, 2),
        ("3 × 3  (9 cells, classic)",         3, 3, 1, 1),
    };

    public SettingsForm(MinimapForm? minimap)
    {
        _minimap = minimap;

        Text = "ScrollVD — Settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        try
        {
            using var s = typeof(SettingsForm).Assembly.GetManifestResourceStream("ScrollVD.icon.ico");
            if (s is not null) Icon = new Icon(s);
        }
        catch { }

        foreach (var (_, lbl) in Hotkeys) { _hotkey.Items.Add(lbl); _mmHotkey.Items.Add(lbl); }
        foreach (var l in Layouts) _layout.Items.Add(l.label);

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // === Section: Panning ===
        Header("Panning");
        Span(_enabled);
        Span(_edge);
        // GRID-MODE-ONLY (testing): hotkey grab options hidden.
        // Span(_grab);
        // Row("Grab hotkey:", _hotkey);
        // Span(_reverse);
        // GRID-MODE-ONLY (testing): grid mode is the only mode now, always on.
        // Span(_snap);

        Row("Grid layout:", _layout);

        Header("Edge scrolling");
        Span(_edgeShift);
        // GRID-MODE-ONLY (testing): smooth scroll speed options hidden.
        // Row("Speed (start), px/tick:", _baseSpeed);
        // Row("Max speed, px/tick:", _maxSpeed);
        // Row("Acceleration, px/tick per sec:", _accel);
        Row("Start delay, ms:", _dwell);
        Row("Dead corners, px:", _corner);
        Row("Edge trigger zone, px:", _margin);

        // === Section: Minimap ===
        Header("Minimap");
        Span(_mmVisible);
        Row("Hotkey (double tap):", _mmHotkey);
        Row("Minimap width, px:", _mmWidth);

        // === Section: System ===
        Header("System");
        Span(_autostart);

        var resetBtn = new Button { Text = "Reset window positions", AutoSize = true };
        resetBtn.Click += (_, _) => Program.ResetPositions();
        Span(resetBtn);

        // === Buttons ===
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        var apply = new Button { Text = "Apply", AutoSize = true };
        ok.Click += (_, _) => { Apply(); Close(); };
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => Apply();

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(ok);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        root.Controls.Add(_grid);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;

        LoadValues();
    }

    // ====== Grid building ======
    private void Header(string title)
    {
        var lbl = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, _row == 0 ? 0 : 12, 0, 2),
            ForeColor = Color.FromArgb(50, 80, 200),
        };
        _grid.Controls.Add(lbl, 0, _row);
        _grid.SetColumnSpan(lbl, 2);
        _row++;
    }

    private void Row(string label, Control control)
    {
        _grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 5, 12, 0),
        }, 0, _row);
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(0, 3, 0, 0);
        _grid.Controls.Add(control, 1, _row);
        _row++;
    }

    private void Span(Control control)
    {
        control.Margin = new Padding(8, 4, 0, 0);
        _grid.Controls.Add(control, 0, _row);
        _grid.SetColumnSpan(control, 2);
        _row++;
    }

    private static NumericUpDown Num(int min, int max) =>
        new() { Minimum = min, Maximum = max, Width = 80, TextAlign = HorizontalAlignment.Right };

    // ====== Load / save ======
    private void LoadValues()
    {
        var s = Config.Current;
        _enabled.Checked = s.Enabled;
        _edge.Checked = s.EdgeEnabled;
        _grab.Checked = s.GrabEnabled;
        _reverse.Checked = s.ReverseGrab;
        _snap.Checked = s.SnapMode;
        _edgeShift.Checked = s.EdgeRequireShift;
        _autostart.Checked = Autostart.IsEnabled();
        _hotkey.SelectedIndex = HotkeyIndex(s.GrabHotkey);

        _baseSpeed.Value = Clamp(_baseSpeed, s.EdgeBaseSpeed);
        _maxSpeed.Value = Clamp(_maxSpeed, s.EdgeMaxSpeed);
        _accel.Value = Clamp(_accel, s.EdgeAccelPerSec);
        _dwell.Value = Clamp(_dwell, s.EdgeDwellMs);
        _corner.Value = Clamp(_corner, s.CornerDead);
        _margin.Value = Clamp(_margin, s.EdgeMargin);
        _layout.SelectedIndex = LayoutIndex(s);

        _mmVisible.Checked = _minimap?.Visible == true;
        _mmHotkey.SelectedIndex = HotkeyIndex(s.MinimapHotkey);
        _mmWidth.Value = Clamp(_mmWidth, s.MinimapWidth);
    }

    private void Apply()
    {
        var s = Config.Current;
        s.Enabled = _enabled.Checked;
        s.EdgeEnabled = _edge.Checked;
        s.GrabEnabled = _grab.Checked;
        s.ReverseGrab = _reverse.Checked;
        s.SnapMode = _snap.Checked;
        s.EdgeRequireShift = _edgeShift.Checked;
        s.GrabHotkey = HotkeyAt(_hotkey.SelectedIndex);

        s.EdgeBaseSpeed = (int)_baseSpeed.Value;
        s.EdgeMaxSpeed = Math.Max((int)_baseSpeed.Value, (int)_maxSpeed.Value);
        s.EdgeAccelPerSec = (int)_accel.Value;
        s.EdgeDwellMs = (int)_dwell.Value;
        s.CornerDead = (int)_corner.Value;
        s.EdgeMargin = (int)_margin.Value;

        // Grid layout — if it changed, bring windows home first so none get stranded
        int li = _layout.SelectedIndex >= 0 ? _layout.SelectedIndex : Layouts.Length - 1;
        var lay = Layouts[li];
        bool gridChanged = s.GridCols != lay.cols || s.GridRows != lay.rows
                        || s.HomeCol != lay.homeCol || s.HomeRow != lay.homeRow;
        if (gridChanged) Program.ResetPositions();
        s.GridCols = lay.cols;
        s.GridRows = lay.rows;
        s.HomeCol = lay.homeCol;
        s.HomeRow = lay.homeRow;

        s.MinimapHotkey = HotkeyAt(_mmHotkey.SelectedIndex);
        int newW = (int)_mmWidth.Value;
        if (newW != s.MinimapWidth && _minimap is not null)
        {
            s.MinimapWidth = newW;
            s.MinimapHeight = -1; // reset — recompute automatically
            _minimap.Size = new Size(newW, -1); // MinimapForm recomputes its own height on the next Show
        }
        s.MinimapWidth = newW;

        Autostart.Set(_autostart.Checked);

        // Sync minimap visibility
        if (_minimap is not null)
        {
            bool want = _mmVisible.Checked;
            if (want && !_minimap.Visible) _minimap.Show();
            else if (!want && _minimap.Visible) _minimap.Hide();
            s.MinimapVisible = want;
        }

        s.Save();
    }

    private static int HotkeyIndex(HotkeyCombo c) => Math.Max(0, Array.FindIndex(Hotkeys, h => h.combo == c));
    private static HotkeyCombo HotkeyAt(int i) => i >= 0 && i < Hotkeys.Length ? Hotkeys[i].combo : HotkeyCombo.CtrlShift;

    private static int LayoutIndex(Settings s)
    {
        int i = Array.FindIndex(Layouts, l =>
            l.cols == s.GridCols && l.rows == s.GridRows && l.homeCol == s.HomeCol && l.homeRow == s.HomeRow);
        return i >= 0 ? i : Layouts.Length - 1; // default to 3×3
    }

    private static decimal Clamp(NumericUpDown n, int v) => Math.Clamp(v, (int)n.Minimum, (int)n.Maximum);
}
