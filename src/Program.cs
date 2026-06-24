using System.Runtime.InteropServices;
using System.Text;

namespace ScrollVD;

internal static class Program
{
    private const int TimerMs = 16; // ~60 frames/sec

    private enum Mode { None, Grab, Edge, Scroll }

    private static PanEngine _engine = null!;
    private static Native.HookProc _hookProc = null!;
    private static IntPtr _hook;
    private static System.Windows.Forms.Timer _timer = null!;

    private static Mode _mode = Mode.None;
    private static Native.POINT _anchor;
    private static bool _pinning;
    private static long _edgeEnterTick;

    // Drag a window onto the minimap to throw it to another grid cell
    private static IntPtr _dragHwnd;
    private static Native.RECT _dragOrigRect;
    private static bool _dragActive;
    private static bool _lmbWasDown;
    private static long _dragEdgeEnterTick; // dwell timer for drag-to-edge panning
    // private static long _edgeStartTick; // GRID-MODE-ONLY (testing): used only by smooth edge scroll

    // Scroll-to-window: ease-out animation
    private static long _scrollTargetX, _scrollTargetY;
    // Detect foreground-window change -> marker on the minimap
    private static IntPtr _prevForeground;
    // Last real (user) foreground window — used by the tray "pull here" item,
    // since opening the tray menu itself changes the foreground.
    private static IntPtr _lastUserWindow;
    private const double ScrollEase = 0.28; // fraction of remaining distance per tick
    private const int ScrollMinStep = 6;    // minimum step px
    private const int ScrollDoneThresh = 3; // treat as done when less than N px remain

    // Double tap for the minimap
    private static bool _snapEdgeDone; // grid mode: already jumped, waiting for the cursor to leave the edge
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
        // Hard single-instance guard: if a copy is already running, exit immediately
        using var mutex = new System.Threading.Mutex(true, @"Global\ScrollVD_SingleInstance", out bool isFirst);
        if (!isFirst) return;

        // Keep the tray app alive if a stray UI exception happens (don't crash the process)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, _) => { };

        ApplicationConfiguration.Initialize();

        // GRID-MODE-ONLY (testing): grid is the only mode; force it on so the
        // minimap snap and edge jumps always behave as grid regardless of saved config.
        S.SnapMode = true;

        _engine = new PanEngine();

        // On startup, rescue any window left off-screen by a previous (possibly crashed)
        // session — across every Windows virtual desktop.
        _engine.Reset(allDesktops: true);

        _hookProc = MouseHook;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            MessageBox.Show("Failed to install the mouse hook. Please start the application again.",
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
            _engine.Reset(allDesktops: true);
        };

        Application.Run();
    }

    // ====== Hotkey grab mode ======
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

                // Interrupt scroll-to-window if the user takes control themselves
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

    // ====== Main tick ======
    private static void OnTick(object? sender, EventArgs e)
    {
        DesktopSwitchTick();
        DragToCellTick();
        ForegroundTick();
        ScrollAnimTick();
        EdgeTick();
        MinimapToggleTick();
    }

    // ====== Detect virtual desktop change ======
    private static void DesktopSwitchTick()
    {
        var id = _engine.CurrentDesktopId();
        if (id == Guid.Empty) return;

        if (_currentDesktopId == Guid.Empty)
        {
            // First desktop detection: just remember it, don't reset anything
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

    // ====== Drag a window onto the minimap → throw it to a grid cell ======
    private static void DragToCellTick()
    {
        bool lmb = Down(0x01);
        Native.GetCursorPos(out var p);
        var pt = new System.Drawing.Point(p.x, p.y);

        if (lmb && !_lmbWasDown)
        {
            // LMB pressed: remember the top-level window under the cursor (if throwable)
            var h = Native.GetAncestor(Native.WindowFromPoint(p), Native.GA_ROOT);
            if (IsThrowable(h))
            {
                _dragHwnd = h;
                Native.GetWindowRect(h, out _dragOrigRect);
                _dragActive = true;
            }
        }
        else if (lmb && _dragActive)
        {
            // Over the minimap → highlight the target cell; otherwise, if at a screen
            // edge, pan one cell (carrying the dragged window with you).
            if (_minimap is { Visible: true } && _minimap.TryGetCellAtScreen(pt, out int col, out int row))
            {
                _minimap.SetHighlight(col, row);
                _dragEdgeEnterTick = 0;
            }
            else
            {
                _minimap?.SetHighlight(-1, -1);
                HandleDragEdgePan(p);
            }
        }
        else if (!lmb && _lmbWasDown && _dragActive)
        {
            // Released: if over a cell, move the window there with its original coords
            if (_minimap is { Visible: true } && _minimap.TryGetCellAtScreen(pt, out int col, out int row))
                _engine.MoveWindowToCell(_dragHwnd, col, row, _dragOrigRect.left, _dragOrigRect.top);
            _minimap?.SetHighlight(-1, -1);
            _dragActive = false;
            _dragHwnd = IntPtr.Zero;
            _dragEdgeEnterTick = 0;
        }

        _lmbWasDown = lmb;
    }

    // While dragging a window to a screen edge, pan one cell in that direction every
    // EdgeDwellMs, excluding the dragged window so it rides along under the cursor.
    private static void HandleDragEdgePan(Native.POINT p)
    {
        if (!S.Enabled) return;

        // Trigger at the work-area edge (just inside the taskbar), so dragging a window
        // down to the taskbar still pans — you can't push the cursor past it.
        var wa = Screen.GetWorkingArea(new System.Drawing.Point(p.x, p.y));
        int m = S.EdgeMargin;
        bool atLeft = p.x <= wa.Left + m, atRight = p.x >= wa.Right - 1 - m;
        bool atTop = p.y <= wa.Top + m, atBottom = p.y >= wa.Bottom - 1 - m;

        int ux = atLeft ? 1 : atRight ? -1 : 0;
        int uy = atTop ? 1 : atBottom ? -1 : 0;
        if ((ux == 0 && uy == 0) || !DesktopEnabled()) { _dragEdgeEnterTick = 0; return; }

        if (_mode == Mode.Scroll) return; // a pan is still animating — wait for it
        if (_dragEdgeEnterTick == 0) { _dragEdgeEnterTick = Environment.TickCount64; return; }
        if (Environment.TickCount64 - _dragEdgeEnterTick < S.EdgeDwellMs) return;

        TriggerSnapJump(ux, uy, _dragHwnd); // animated pan, dragged window stays put
        _dragEdgeEnterTick = Environment.TickCount64; // re-arm for the next cell
    }

    private static bool IsThrowable(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        if (_minimap is not null && h == _minimap.Handle) return false;
        if (_settings is { IsDisposed: false } && h == _settings.Handle) return false;
        return IsCandidateForScroll(h);
    }

    // ====== Active window changed: bring it from another cell + marker on the minimap ======
    private static void ForegroundTick()
    {
        if (!S.Enabled) return;

        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _prevForeground) return;
        _prevForeground = fg;

        // Ignore system and unnamed windows (popup menus, tray flyouts, etc.)
        if (!IsCandidateForScroll(fg)) return;
        _lastUserWindow = fg;

        // If the window is on another cell (selected from the taskbar but it's off-screen),
        // bring it to the current screen. Only this window moves.
        _engine.PullWindowToCurrentScreen(fg);

        // Keep the minimap above the newly-activated window, then mark where it is
        if (_minimap is { Visible: true })
        {
            _minimap.ReassertTopMost();
            if (Native.GetWindowRect(fg, out var r))
            {
                int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                var (offX, offY, _, _) = _engine.GetState();
                long cx = (r.left + r.right) / 2 - vx - offX;
                long cy = (r.top + r.bottom) / 2 - vy - offY;
                _minimap.ShowMarker(cx, cy);
            }
        }
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
            or "#32768"); // popup menu
    }

    // ====== Scroll animation (ease-out, used by the grid jump) ======
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

        // Safety net: if the offset didn't change (clamped at the canvas bound),
        // the target is unreachable — stop instead of spinning forever.
        var (nx, ny, _, _) = _engine.GetState();
        if (nx == offX && ny == offY) EndScroll();
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

    // ====== Edge scroll (with acceleration or grid jump) ======
    private static void EdgeTick()
    {
        if (!S.Enabled || !S.EdgeEnabled || _mode == Mode.Grab || IsGrabDown() || Down(0x01))
        {
            EndEdge();
            return;
        }

        // Optional: only allow edge jumps while Shift is held
        if (S.EdgeRequireShift && !Down(Native.VK_SHIFT))
        {
            EndEdge();
            return;
        }

        // While the scroll animation is running, wait for it to finish and reset dwell
        if (_mode == Mode.Scroll)
        {
            _edgeEnterTick = 0;
            return; // don't touch _snapEdgeDone — the cursor hasn't left the edge
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

    /// <summary>Jump exactly one screen in the (ux, uy) direction (animated). If
    /// <paramref name="exclude"/> is set, that window stays put while the rest pan.</summary>
    private static void TriggerSnapJump(int ux, int uy, IntPtr exclude = default)
    {
        var (offX, offY, maxX, maxY) = _engine.GetState();
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) return;

        // Current grid cell (nearest multiple of sw/sh)
        int cellX = (int)Math.Round((double)offX / sw);
        int cellY = (int)Math.Round((double)offY / sh);

        // Target cell, clamped to the canvas bounds so it's always reachable
        // (otherwise the scroll animation would chase an offset Shift() can never reach).
        long tX = Math.Clamp((long)(cellX + ux) * sw, -maxX, maxX);
        long tY = Math.Clamp((long)(cellY + uy) * sh, -maxY, maxY);

        if (tX == offX && tY == offY) return; // already at the canvas edge

        _scrollTargetX = tX;
        _scrollTargetY = tY;
        _mode = Mode.Scroll;
        _engine.BeginGesture();
        if (exclude != IntPtr.Zero) _engine.ExcludeFromGesture(exclude);
    }

    private static void EndEdge()
    {
        if (_mode == Mode.Edge) { _engine.EndGesture(); _mode = Mode.None; }
        _edgeEnterTick = 0;
        _snapEdgeDone = false; // cursor left — allow the next jump
    }

    // ====== Minimap hotkey double tap ======
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

    // ====== Key helpers ======
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

    // ====== Tray ======
    private static void BuildTray()
    {
        var menu = new ContextMenuStrip();

        _miEnabled = new ToolStripMenuItem("Enabled", null, (_, _) =>
        {
            S.Enabled = !S.Enabled;
            if (!S.Enabled && _mode != Mode.None) { EndScroll(); EndEdge(); _engine.EndGesture(); _mode = Mode.None; }
            S.Save();
        });

        _miEdge = new ToolStripMenuItem("Edge scrolling", null, (_, _) =>
        { S.EdgeEnabled = !S.EdgeEnabled; S.Save(); });

        _miThisDesktop = new ToolStripMenuItem("Panning on this desktop", null, (_, _) =>
        {
            var id = _engine.CurrentDesktopId();
            if (id == Guid.Empty) return;
            if (!S.DisabledDesktops.Remove(id)) S.DisabledDesktops.Add(id);
            S.Save();
        });

        _miAutostart = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
            Autostart.Set(!Autostart.IsEnabled()));

        _miMinimap = new ToolStripMenuItem("Show minimap", null, (_, _) =>
        { _minimap?.ToggleVisible(); UpdateMinimapTrayItem(); });

        var miPull = new ToolStripMenuItem("Bring active window here", null, (_, _) =>
        {
            if (_lastUserWindow != IntPtr.Zero)
                _engine.PullWindowToCurrentScreen(_lastUserWindow);
        });

        var miSettings = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings())
            { Font = new Font(menu.Font, FontStyle.Bold) };
        var miReset = new ToolStripMenuItem("Reset window positions", null, (_, _) => _engine.Reset());
        var miResetAll = new ToolStripMenuItem("Reset window positions (all desktops)", null, (_, _) => _engine.Reset(allDesktops: true));
        var miExit = new ToolStripMenuItem("Exit", null, (_, _) => Application.Exit());

        menu.Items.Add(miSettings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miEnabled);
        menu.Items.Add(_miEdge);
        menu.Items.Add(_miThisDesktop);
        menu.Items.Add(_miAutostart);
        menu.Items.Add(_miMinimap);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miPull);
        menu.Items.Add(miReset);
        menu.Items.Add(miResetAll);
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
            Text = "ScrollVD — panoramic desktop",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private static void UpdateMinimapTrayItem()
    {
        if (_miMinimap is null) return;
        bool visible = _minimap?.Visible == true;
        _miMinimap.Text = visible ? "Hide minimap" : "Show minimap";
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
