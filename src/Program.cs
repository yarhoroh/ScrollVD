using System.Runtime.InteropServices;
using System.Text;

namespace ScrollVD;

internal static class Program
{
    private const int TimerMs = 16; // ~60 кадров/с

    private enum Mode { None, Grab, Edge, Scroll }

    private static PanEngine _engine = null!;
    private static Native.HookProc _hookProc = null!;
    private static IntPtr _hook;
    private static System.Windows.Forms.Timer _timer = null!;

    private static Mode _mode = Mode.None;
    private static Native.POINT _anchor;
    private static bool _pinning;
    private static long _edgeEnterTick;
    // private static long _edgeStartTick; // GRID-MODE-ONLY (testing): used only by smooth edge scroll

    // Прокрутка к окну: анимация ease-out
    private static long _scrollTargetX, _scrollTargetY;
    // Детект смены foreground-окна → метка на миникарте
    private static IntPtr _prevForeground;
    private const double ScrollEase = 0.28; // доля оставшегося расстояния за тик
    private const int ScrollMinStep = 6;    // минимальный шаг px
    private const int ScrollDoneThresh = 3; // считаем завершённым, если осталось < N px

    // Двойной тап для миникарты
    private static bool _snapEdgeDone; // в режиме сетки: уже прыгнули, ждём ухода курсора с края
    private static Guid _currentDesktopId = Guid.Empty;

    private static bool _mmHotkeyWasDown;
    private static long _mmFirstTapTick;
    private static int _mmTapCount;

    private static NotifyIcon _tray = null!;
    private static ToolStripMenuItem _miEnabled = null!;
    private static ToolStripMenuItem _miEdge = null!;
    private static ToolStripMenuItem _miThisDesktop = null!;
    private static ToolStripMenuItem _miAutostart = null!;
    private static ToolStripMenuItem _miMinimap = null!;
    private static SettingsForm? _settings;
    private static MinimapForm? _minimap;

    private static Settings S => Config.Current;

    [STAThread]
    static void Main()
    {
        // Жёсткий запрет второго запуска: если копия уже работает — сразу выходим
        using var mutex = new System.Threading.Mutex(true, @"Global\ScrollVD_SingleInstance", out bool isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();

        // GRID-MODE-ONLY (testing): grid is the only mode; force it on so the
        // minimap snap and edge jumps always behave as grid regardless of saved config.
        S.SnapMode = true;

        _engine = new PanEngine();

        // Single reset entry point: on startup it rescues any window left off-screen
        // by a previous (possibly crashed) session.
        _engine.Reset();

        _hookProc = MouseHook;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            MessageBox.Show("Не удалось установить хук мыши. Запустите приложение ещё раз.",
                "ScrollVD", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _minimap = new MinimapForm(_engine);
        if (S.MinimapVisible) _minimap.Show();

        _timer = new System.Windows.Forms.Timer { Interval = TimerMs };
        _timer.Tick += OnTick;
        _timer.Start();

        BuildTray();

        Application.ApplicationExit += (_, _) =>
        {
            _timer.Stop();
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _tray.Visible = false;
            _engine.Reset();
        };

        Application.Run();
    }

    // ====== Режим захвата по горячей клавише ======
    private static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _pinning)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        if ((int)wParam == Native.WM_MOUSEMOVE)
        {
            // GRID-MODE-ONLY (testing): hotkey grab disabled. Original line kept below.
            // bool grab = S.Enabled && S.GrabEnabled && !S.SnapMode && IsGrabDown();
            bool grab = false;

            if (grab && DesktopEnabled())
            {
                var pt = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam).pt;

                // Прерываем прокрутку-к-окну если пользователь сам взял управление
                if (_mode == Mode.Scroll) EndScroll();
                if (_mode == Mode.Edge) { _engine.EndGesture(); _mode = Mode.None; }

                if (_mode != Mode.Grab)
                {
                    _mode = Mode.Grab;
                    _anchor = pt;
                    _engine.BeginGesture();
                    return (IntPtr)1;
                }

                int dx = pt.x - _anchor.x;
                int dy = pt.y - _anchor.y;
                if (S.ReverseGrab) { dx = -dx; dy = -dy; }
                if (dx != 0 || dy != 0)
                {
                    _engine.Shift(dx, dy);
                    _pinning = true;
                    Native.SetCursorPos(_anchor.x, _anchor.y);
                    _pinning = false;
                }
                return (IntPtr)1;
            }

            if (_mode == Mode.Grab) { _mode = Mode.None; _engine.EndGesture(); }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ====== Главный тик ======
    private static void OnTick(object? sender, EventArgs e)
    {
        DesktopSwitchTick();
        ForegroundTick();
        ScrollAnimTick();
        EdgeTick();
        MinimapToggleTick();
    }

    // ====== Детект смены виртуального рабочего стола ======
    private static void DesktopSwitchTick()
    {
        var id = _engine.CurrentDesktopId();
        if (id == Guid.Empty) return;

        if (_currentDesktopId == Guid.Empty)
        {
            // Первое определение рабочего стола — просто запоминаем, ничего не сбрасываем
            _currentDesktopId = id;
            return;
        }

        if (id == _currentDesktopId) return;

        var prev = _currentDesktopId;
        _currentDesktopId = id;

        EndScroll();
        EndEdge();
        _engine.OnDesktopSwitch(prev, id);
        _prevForeground = IntPtr.Zero;
    }

    // ====== Детект смены активного окна → красная метка на миникарте ======
    private static void ForegroundTick()
    {
        if (!S.Enabled) return;
        if (_minimap is not { Visible: true }) return;

        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _prevForeground) return;
        _prevForeground = fg;

        // Игнорируем системные и безымянные окна (попап-меню, флайаут трея и т.п.)
        if (!IsCandidateForScroll(fg)) return;
        if (!Native.GetWindowRect(fg, out var r)) return;

        // Центр окна → координаты холста (вычитаем текущее смещение панорамы)
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var (offX, offY, _, _) = _engine.GetState();
        long cx = (r.left + r.right) / 2 - vx - offX;
        long cy = (r.top + r.bottom) / 2 - vy - offY;
        _minimap.ShowMarker(cx, cy);
    }

    private static bool IsCandidateForScroll(IntPtr hWnd)
    {
        if (!Native.IsWindowVisible(hWnd)) return false;
        if (Native.IsIconic(hWnd)) return false;
        if (Native.GetWindowTextLength(hWnd) == 0) return false;
        var sb = new StringBuilder(64);
        Native.GetClassName(hWnd, sb, sb.Capacity);
        var cls = sb.ToString();
        return cls is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Progman" or "WorkerW"
            or "Windows.UI.Core.CoreWindow"
            or "XamlExplorerHostIslandWindow"
            or "#32768"); // popup-меню
    }

    // ====== Анимация прокрутки (ease-out, используется прыжком по сетке) ======
    private static void ScrollAnimTick()
    {
        if (_mode != Mode.Scroll) return;

        var (offX, offY, _, _) = _engine.GetState();
        long remX = _scrollTargetX - offX;
        long remY = _scrollTargetY - offY;

        if (Math.Abs(remX) <= ScrollDoneThresh && Math.Abs(remY) <= ScrollDoneThresh)
        {
            _engine.Shift((int)remX, (int)remY);
            EndScroll();
            return;
        }

        int stepX = StepFor(remX);
        int stepY = StepFor(remY);
        _engine.Shift(stepX, stepY);
    }

    private static int StepFor(long rem)
    {
        if (rem == 0) return 0;
        int step = (int)Math.Max(ScrollMinStep, Math.Abs(rem) * ScrollEase);
        return rem > 0 ? step : -step;
    }

    private static void EndScroll()
    {
        if (_mode == Mode.Scroll) { _engine.EndGesture(); _mode = Mode.None; }
    }

    // ====== Прокрутка у края (с ускорением или прыжком по сетке) ======
    private static void EdgeTick()
    {
        if (!S.Enabled || !S.EdgeEnabled || _mode == Mode.Grab || IsGrabDown() || Down(0x01))
        {
            EndEdge();
            return;
        }

        // Пока идёт анимация прокрутки — ждём завершения, dwell сбрасываем
        if (_mode == Mode.Scroll)
        {
            _edgeEnterTick = 0;
            return; // _snapEdgeDone не трогаем — курсор не уходил с края
        }

        Native.GetCursorPos(out var p);

        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int right = vx + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) - 1;
        int bottom = vy + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN) - 1;

        int margin = S.EdgeMargin;
        bool atLeft = p.x <= vx + margin;
        bool atRight = p.x >= right - margin;
        bool atTop = p.y <= vy + margin;
        bool atBottom = p.y >= bottom - margin;

        // Ignore left/right/top edges over the taskbar (outside the monitor work area).
        // The bottom edge is intentionally NOT guarded so bottom scroll reaches the
        // very bottom of the screen, even over a bottom taskbar.
        var wa = Screen.GetWorkingArea(new System.Drawing.Point(p.x, p.y));
        if (p.y <  wa.Top)   atTop   = false;
        if (p.x <  wa.Left)  atLeft  = false;
        if (p.x >= wa.Right) atRight = false;

        int ux = atLeft ? 1 : atRight ? -1 : 0;
        int uy = atTop ? 1 : atBottom ? -1 : 0;

        int dead = S.CornerDead;
        bool nearCorner = (ux != 0 && uy != 0)
            || ((p.x <= vx + dead || p.x >= right - dead) && (p.y <= vy + dead || p.y >= bottom - dead));

        if ((ux == 0 && uy == 0) || nearCorner || !DesktopEnabled())
        {
            EndEdge();
            return;
        }

        // ---- Grid mode (only mode kept during testing) ----
        if (_snapEdgeDone) return; // wait until the cursor leaves the edge

        if (_edgeEnterTick == 0) { _edgeEnterTick = Environment.TickCount64; return; }
        if (Environment.TickCount64 - _edgeEnterTick < S.EdgeDwellMs) return;

        TriggerSnapJump(ux, uy);
        _snapEdgeDone = true;
        return;

        // ---- GRID-MODE-ONLY (testing): smooth edge scroll disabled. ----
        // if (_mode != Mode.Edge)
        // {
        //     if (_edgeEnterTick == 0) { _edgeEnterTick = Environment.TickCount64; return; }
        //     if (Environment.TickCount64 - _edgeEnterTick < S.EdgeDwellMs) return;
        //     _engine.BeginGesture();
        //     _mode = Mode.Edge;
        //     _edgeStartTick = Environment.TickCount64;
        // }
        //
        // double heldSec = (Environment.TickCount64 - _edgeStartTick) / 1000.0;
        // double speed = Math.Min(S.EdgeMaxSpeed, S.EdgeBaseSpeed + S.EdgeAccelPerSec * heldSec);
        // _engine.Shift(ux * (int)Math.Max(1, Math.Round(speed)), uy * (int)Math.Max(1, Math.Round(speed)));
    }

    /// <summary>Прыгнуть ровно на один экран в направлении (ux, uy).</summary>
    private static void TriggerSnapJump(int ux, int uy)
    {
        var (offX, offY, _, _) = _engine.GetState();
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        // Текущая ячейка сетки (ближайший кратный sw/sh)
        int cellX = (int)Math.Round((double)offX / sw);
        int cellY = (int)Math.Round((double)offY / sh);

        long tX = (long)(cellX + ux) * sw;
        long tY = (long)(cellY + uy) * sh;

        if (tX == offX && tY == offY) return; // уже у границы холста

        _scrollTargetX = tX;
        _scrollTargetY = tY;
        _mode = Mode.Scroll;
        _engine.BeginGesture();
    }

    private static void EndEdge()
    {
        if (_mode == Mode.Edge) { _engine.EndGesture(); _mode = Mode.None; }
        _edgeEnterTick = 0;
        _snapEdgeDone = false; // курсор ушёл — разрешить следующий прыжок
    }

    // ====== Двойной тап hotkey миникарты ======
    private static void MinimapToggleTick()
    {
        bool down = IsMinimapHotkeyDown();
        if (down && !_mmHotkeyWasDown)
        {
            long now = Environment.TickCount64;
            if (_mmTapCount == 0 || now - _mmFirstTapTick > 600)
            {
                _mmFirstTapTick = now;
                _mmTapCount = 1;
            }
            else
            {
                _mmTapCount++;
                if (_mmTapCount >= 2)
                {
                    _mmTapCount = 0;
                    _minimap?.ToggleVisible();
                    UpdateMinimapTrayItem();
                }
            }
        }
        _mmHotkeyWasDown = down;
    }

    // ====== Хелперы клавиш ======
    private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsGrabDown()
    {
        bool ctrl = Down(Native.VK_CONTROL), shift = Down(Native.VK_SHIFT);
        bool alt = Down(Native.VK_MENU), win = Down(Native.VK_LWIN) || Down(Native.VK_RWIN);
        return S.GrabHotkey switch
        {
            HotkeyCombo.CtrlShift => ctrl && shift,
            HotkeyCombo.CtrlAlt   => ctrl && alt,
            HotkeyCombo.ShiftAlt  => shift && alt,
            HotkeyCombo.WinShift  => win && shift,
            HotkeyCombo.CtrlWin   => ctrl && win,
            _ => false
        };
    }

    private static bool IsMinimapHotkeyDown()
    {
        bool ctrl = Down(Native.VK_CONTROL), shift = Down(Native.VK_SHIFT);
        bool alt = Down(Native.VK_MENU), win = Down(Native.VK_LWIN) || Down(Native.VK_RWIN);
        return S.MinimapHotkey switch
        {
            HotkeyCombo.CtrlShift => ctrl && shift,
            HotkeyCombo.CtrlAlt   => ctrl && alt,
            HotkeyCombo.ShiftAlt  => shift && alt,
            HotkeyCombo.WinShift  => win && shift,
            HotkeyCombo.CtrlWin   => ctrl && win,
            _ => false
        };
    }

    private static bool DesktopEnabled()
    {
        var id = _engine.CurrentDesktopId();
        return id == Guid.Empty || !S.DisabledDesktops.Contains(id);
    }

    // ====== Трей ======
    private static void BuildTray()
    {
        var menu = new ContextMenuStrip();

        _miEnabled = new ToolStripMenuItem("Включено", null, (_, _) =>
        {
            S.Enabled = !S.Enabled;
            if (!S.Enabled && _mode != Mode.None) { EndScroll(); EndEdge(); _engine.EndGesture(); _mode = Mode.None; }
            S.Save();
        });

        _miEdge = new ToolStripMenuItem("Прокрутка у края экрана", null, (_, _) =>
        { S.EdgeEnabled = !S.EdgeEnabled; S.Save(); });

        _miThisDesktop = new ToolStripMenuItem("Панорама на этом рабочем столе", null, (_, _) =>
        {
            var id = _engine.CurrentDesktopId();
            if (id == Guid.Empty) return;
            if (!S.DisabledDesktops.Remove(id)) S.DisabledDesktops.Add(id);
            S.Save();
        });

        _miAutostart = new ToolStripMenuItem("Запускать с Windows", null, (_, _) =>
            Autostart.Set(!Autostart.IsEnabled()));

        _miMinimap = new ToolStripMenuItem("Показать миникарту", null, (_, _) =>
        { _minimap?.ToggleVisible(); UpdateMinimapTrayItem(); });

        var miSettings = new ToolStripMenuItem("Настройки…", null, (_, _) => OpenSettings())
            { Font = new Font(menu.Font, FontStyle.Bold) };
        var miReset = new ToolStripMenuItem("Сбросить позиции окон", null, (_, _) => _engine.Reset());
        var miExit = new ToolStripMenuItem("Выход", null, (_, _) => Application.Exit());

        menu.Items.Add(miSettings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miEnabled);
        menu.Items.Add(_miEdge);
        menu.Items.Add(_miThisDesktop);
        menu.Items.Add(_miAutostart);
        menu.Items.Add(_miMinimap);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miReset);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        menu.Opening += (_, _) =>
        {
            var id = _engine.CurrentDesktopId();
            _miEnabled.Checked = S.Enabled;
            _miEdge.Checked = S.EdgeEnabled;
            _miThisDesktop.Enabled = id != Guid.Empty;
            _miThisDesktop.Checked = id == Guid.Empty || !S.DisabledDesktops.Contains(id);
            _miAutostart.Checked = Autostart.IsEnabled();
            UpdateMinimapTrayItem();
        };

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ScrollVD — панорамный рабочий стол",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private static void UpdateMinimapTrayItem()
    {
        if (_miMinimap is null) return;
        bool visible = _minimap?.Visible == true;
        _miMinimap.Text = visible ? "Скрыть миникарту" : "Показать миникарту";
        _miMinimap.Checked = visible;
    }

    private static void OpenSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(_minimap);
        _settings.Show();
    }

    /// <summary>Reset all windows to home positions (same as the tray menu item).</summary>
    internal static void ResetPositions() => _engine.Reset();

    private static Icon LoadAppIcon()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("ScrollVD.icon.ico");
        return s is not null ? new Icon(s) : SystemIcons.Application;
    }
}
