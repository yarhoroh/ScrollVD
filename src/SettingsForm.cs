namespace ScrollVD;

internal sealed class SettingsForm : Form
{
    private readonly MinimapForm? _minimap;

    // --- Панорама ---
    private readonly CheckBox _enabled = new() { Text = "Панорама включена", AutoSize = true };
    private readonly CheckBox _edge = new() { Text = "Прокрутка у края экрана", AutoSize = true };
    private readonly CheckBox _grab = new() { Text = "Захват по горячей клавише", AutoSize = true };
    private readonly CheckBox _reverse = new() { Text = "Обратное направление захвата", AutoSize = true };
    private readonly CheckBox _snap = new() { Text = "Режим сетки — прыжок целым экраном (9 областей)", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Запускать вместе с Windows", AutoSize = true };
    private readonly ComboBox _hotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };

    private readonly NumericUpDown _baseSpeed = Num(1, 200);
    private readonly NumericUpDown _maxSpeed = Num(1, 400);
    private readonly NumericUpDown _accel = Num(0, 500);
    private readonly NumericUpDown _dwell = Num(0, 2000);
    private readonly NumericUpDown _corner = Num(0, 400);
    private readonly NumericUpDown _margin = Num(1, 50);
    private readonly NumericUpDown _canvas = Num(1, 10);

    // --- Миникарта ---
    private readonly CheckBox _mmVisible = new() { Text = "Показать миникарту", AutoSize = true };
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

    public SettingsForm(MinimapForm? minimap)
    {
        _minimap = minimap;

        Text = "ScrollVD — настройки";
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

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // === Секция: Панорама ===
        Header("Панорама");
        Span(_enabled);
        Span(_edge);
        // GRID-MODE-ONLY (testing): hotkey grab options hidden.
        // Span(_grab);
        // Row("Горячая клавиша захвата:", _hotkey);
        // Span(_reverse);
        // GRID-MODE-ONLY (testing): grid mode is the only mode now, always on.
        // Span(_snap);

        Header("Прокрутка у края");
        // GRID-MODE-ONLY (testing): smooth scroll speed options hidden.
        // Row("Скорость (старт), px/такт:", _baseSpeed);
        // Row("Максимальная скорость, px/такт:", _maxSpeed);
        // Row("Ускорение, px/такт за сек.:", _accel);
        Row("Задержка перед стартом, мс:", _dwell);
        Row("«Мёртвые» углы, px:", _corner);
        Row("Зона срабатывания у края, px:", _margin);
        Row("Размер холста (× экрана):", _canvas);

        // === Секция: Миникарта ===
        Header("Миникарта");
        Span(_mmVisible);
        Row("Горячая клавиша (двойной тап):", _mmHotkey);
        Row("Ширина миникарты, px:", _mmWidth);

        // === Секция: Система ===
        Header("Система");
        Span(_autostart);

        var resetBtn = new Button { Text = "Сбросить позиции окон", AutoSize = true };
        resetBtn.Click += (_, _) => Program.ResetPositions();
        Span(resetBtn);

        // === Кнопки ===
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true };
        var apply = new Button { Text = "Применить", AutoSize = true };
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

    // ====== Построение сетки ======
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

    // ====== Загрузка / сохранение ======
    private void LoadValues()
    {
        var s = Config.Current;
        _enabled.Checked = s.Enabled;
        _edge.Checked = s.EdgeEnabled;
        _grab.Checked = s.GrabEnabled;
        _reverse.Checked = s.ReverseGrab;
        _snap.Checked = s.SnapMode;
        _autostart.Checked = Autostart.IsEnabled();
        _hotkey.SelectedIndex = HotkeyIndex(s.GrabHotkey);

        _baseSpeed.Value = Clamp(_baseSpeed, s.EdgeBaseSpeed);
        _maxSpeed.Value = Clamp(_maxSpeed, s.EdgeMaxSpeed);
        _accel.Value = Clamp(_accel, s.EdgeAccelPerSec);
        _dwell.Value = Clamp(_dwell, s.EdgeDwellMs);
        _corner.Value = Clamp(_corner, s.CornerDead);
        _margin.Value = Clamp(_margin, s.EdgeMargin);
        _canvas.Value = Clamp(_canvas, s.CanvasFactor);

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
        s.GrabHotkey = HotkeyAt(_hotkey.SelectedIndex);

        s.EdgeBaseSpeed = (int)_baseSpeed.Value;
        s.EdgeMaxSpeed = Math.Max((int)_baseSpeed.Value, (int)_maxSpeed.Value);
        s.EdgeAccelPerSec = (int)_accel.Value;
        s.EdgeDwellMs = (int)_dwell.Value;
        s.CornerDead = (int)_corner.Value;
        s.EdgeMargin = (int)_margin.Value;
        s.CanvasFactor = (int)_canvas.Value;

        s.MinimapHotkey = HotkeyAt(_mmHotkey.SelectedIndex);
        int newW = (int)_mmWidth.Value;
        if (newW != s.MinimapWidth && _minimap is not null)
        {
            s.MinimapWidth = newW;
            s.MinimapHeight = -1; // сбросить — пересчитать авто
            _minimap.Size = new Size(newW, -1); // MinimapForm пересчитает высоту сама при следующем Show
        }
        s.MinimapWidth = newW;

        Autostart.Set(_autostart.Checked);

        // Синхронизировать видимость миникарты
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
    private static decimal Clamp(NumericUpDown n, int v) => Math.Clamp(v, (int)n.Minimum, (int)n.Maximum);
}
