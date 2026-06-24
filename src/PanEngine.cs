using System.Text;

namespace ScrollVD;

internal sealed class PanEngine
{
    // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER
    private const uint MoveFlags =
        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;

    private readonly Native.IVirtualDesktopManager? _vdm;

    // List of windows in EnumWindows order (TOP → BOTTOM, i.e. [0] = topmost)
    private readonly List<IntPtr> _gesture = new();

    // Current positions — updated incrementally, no GetWindowRect per frame
    private readonly Dictionary<IntPtr, (int x, int y)> _gesturePos = new();

    // Original positions before the first offset — for an accurate Reset()
    private readonly Dictionary<IntPtr, (int x, int y)> _origin = new();

    private long _offX, _offY;
    private int _maxX, _maxY;

    // Saved canvas positions for each virtual desktop
    private readonly Dictionary<Guid, (long offX, long offY)> _desktopOffsets = new();

    public (long offX, long offY, int maxX, int maxY) GetState() => (_offX, _offY, _maxX, _maxY);

    public PanEngine()
    {
        try { _vdm = (Native.IVirtualDesktopManager)new Native.VirtualDesktopManager(); }
        catch { _vdm = null; }
        _maxX = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _maxY = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
    }

    public Guid CurrentDesktopId()
    {
        if (_vdm is null) return Guid.Empty;
        // Fast path: foreground window
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && _vdm.GetWindowDesktopId(fg, out var id) == 0 && id != Guid.Empty)
                return id;
        }
        catch { }
        // Fallback: find the first visible normal window that has a desktop ID
        var sb = new System.Text.StringBuilder(64);
        Guid found = Guid.Empty;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.GetWindowTextLength(h) == 0)
                return true;
            sb.Clear(); Native.GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls == "Shell_TrayWnd" || cls == "Progman" || cls == "WorkerW") return true;
            try
            {
                if (_vdm.GetWindowDesktopId(h, out var id) == 0 && id != Guid.Empty)
                { found = id; return false; }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 1-based number of the current Windows virtual desktop, or 0 if unknown.
    /// The public COM interface only exposes a GUID, so we match it against the
    /// ordered desktop list Windows stores in the registry.
    /// </summary>
    public int CurrentDesktopNumber()
    {
        var id = CurrentDesktopId();
        if (id == Guid.Empty) return 0;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is byte[] blob && blob.Length % 16 == 0)
            {
                for (int i = 0; i < blob.Length / 16; i++)
                {
                    var g = new Guid(blob.AsSpan(i * 16, 16).ToArray());
                    if (g == id) return i + 1;
                }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Number of Windows virtual desktops (from the registry), or 0 if unknown.</summary>
    public int VirtualDesktopCount()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is byte[] blob && blob.Length % 16 == 0)
                return blob.Length / 16;
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Begin a gesture: enumerate windows and record their current positions.
    /// GetWindowRect is called only here — once per gesture.
    /// </summary>
    public void BeginGesture()
    {
        int factor = Math.Max(1, Config.Current.CanvasFactor);
        _maxX = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) * factor;
        _maxY = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN) * factor;

        _gesture.Clear();
        _gesturePos.Clear();

        var sb = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            if (IsMovable(h, sb) && Native.GetWindowRect(h, out var r))
            {
                _gesture.Add(h);
                _gesturePos[h] = (r.left, r.top);
                if (!_origin.ContainsKey(h))
                    _origin[h] = (r.left, r.top);
            }
            return true;
        }, IntPtr.Zero);
    }

    public void Shift(int dx, int dy)
    {
        int adx = (int)(Math.Clamp(_offX + dx, -_maxX, _maxX) - _offX);
        int ady = (int)(Math.Clamp(_offY + dy, -_maxY, _maxY) - _offY);
        if (adx == 0 && ady == 0) return;

        MoveBy(adx, ady);
        _offX += adx;
        _offY += ady;
    }

    public void JumpTo(long targetOffX, long targetOffY)
        => Shift((int)(targetOffX - _offX), (int)(targetOffY - _offY));

    /// <summary>
    /// Pan one grid cell in (ux,uy), but DON'T move <paramref name="exclude"/> — used
    /// while dragging a window to a screen edge so it stays under the cursor (you carry
    /// it into the revealed cell while everything else pans away). Instant, clamped.
    /// </summary>
    public void JumpOneCellExcluding(int ux, int uy, IntPtr exclude)
    {
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) return;

        BeginGesture();                       // populates _gesture and refreshes _maxX
        _gesture.Remove(exclude);
        _gesturePos.Remove(exclude);

        int cellX = (int)Math.Round((double)_offX / sw);
        int cellY = (int)Math.Round((double)_offY / sh);
        long tX = Math.Clamp((long)(cellX + ux) * sw, -_maxX, _maxX);
        long tY = Math.Clamp((long)(cellY + uy) * sh, -_maxY, _maxY);
        JumpTo(tX, tY);
        EndGesture();
    }

    /// <summary>
    /// If the window sits on another cell (doesn't intersect the visible screen),
    /// pull it onto the current screen keeping its sub-screen position. Returns true
    /// if it was moved. Only this one window moves; the global pan is untouched.
    /// </summary>
    public bool PullWindowToCurrentScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE); // un-minimize first
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) return false;

        bool intersects = r.right > vx && r.left < vx + sw && r.bottom > vy && r.top < vy + sh;
        if (!intersects)
        {
            int relX = (((r.left - vx) % sw) + sw) % sw; // wrap into [0, sw)
            int relY = (((r.top - vy) % sh) + sh) % sh;
            Native.SetWindowPos(hwnd, IntPtr.Zero, vx + relX, vy + relY, 0, 0, MoveFlags);
        }

        // Raise it so it's clearly visible — confirms the window was pulled here
        ForceForeground(hwnd);
        return !intersects;
    }

    /// <summary>
    /// Bring a window to the foreground reliably. Plain SetForegroundWindow is blocked
    /// for background processes, so we briefly attach to the current foreground thread.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        var fg = Native.GetForegroundWindow();
        uint thisThread = Native.GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out _) : 0;

        if (fgThread != 0 && fgThread != thisThread) Native.AttachThreadInput(thisThread, fgThread, true);
        Native.BringWindowToTop(hwnd);
        Native.SetForegroundWindow(hwnd);
        if (fgThread != 0 && fgThread != thisThread) Native.AttachThreadInput(thisThread, fgThread, false);
    }

    /// <summary>
    /// For each grid cell, the distinct app icon handles (HICON) of the windows in it
    /// (by window centre). Lets the minimap show what's where without previewing.
    /// HICONs are owned by the windows — do not destroy them.
    /// </summary>
    public Dictionary<(int col, int row), List<IntPtr>> CellIcons()
    {
        var map = new Dictionary<(int, int), List<IntPtr>>();
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) return map;

        int cols = (int)Math.Round(2.0 * _maxX / sw) + 1;
        int rows = (int)Math.Round(2.0 * _maxY / sh) + 1;

        var sb = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            if (!IsMovable(h, sb)) return true;
            if (!Native.GetWindowRect(h, out var r)) return true;
            int cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
            int col = (int)Math.Floor((cx - vx - (double)_offX + _maxX) / sw);
            int row = (int)Math.Floor((cy - vy - (double)_offY + _maxY) / sh);
            if (col < 0 || col >= cols || row < 0 || row >= rows) return true;

            IntPtr icon = Native.GetClassLongPtr(h, Native.GCLP_HICONSM);
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, Native.GCLP_HICON);
            if (icon == IntPtr.Zero) return true;

            if (!map.TryGetValue((col, row), out var list)) { list = new List<IntPtr>(); map[(col, row)] = list; }
            if (!list.Contains(icon)) list.Add(icon); // distinct apps only
            return true;
        }, IntPtr.Zero);
        return map;
    }

    /// <summary>
    /// All movable windows on the current virtual desktop (including minimized ones),
    /// labelled "App name — window title".
    /// </summary>
    public List<(IntPtr hwnd, string title)> ListMovableWindows()
    {
        var result = new List<(IntPtr, string)>();
        var sb = new StringBuilder(64);
        var title = new StringBuilder(256);
        Native.EnumWindows((h, _) =>
        {
            if (IsMovable(h, sb, includeMinimized: true))
            {
                title.Clear();
                Native.GetWindowText(h, title, title.Capacity);
                string t = title.ToString();
                string app = GetAppName(h);
                string label =
                    !string.IsNullOrWhiteSpace(app) && !string.IsNullOrWhiteSpace(t) ? $"{app} — {t}" :
                    !string.IsNullOrWhiteSpace(app) ? app :
                    !string.IsNullOrWhiteSpace(t) ? t : "(untitled)";
                result.Add((h, label));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Process name for the window (e.g. "chrome" → "Chrome"). Uses only ProcessName —
    /// MainModule/FileVersionInfo is far too slow (and throws across bitness/elevation).
    /// </summary>
    private static string GetAppName(IntPtr h)
    {
        try
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Move a window to grid cell (col,row), preserving its position WITHIN a cell
    /// (works no matter which cell it currently sits on). If the target cell is the
    /// one currently in view, the window is also raised to the front.
    /// </summary>
    public void MoveWindowToCellWrapped(IntPtr hwnd, int col, int row)
    {
        if (hwnd == IntPtr.Zero) return;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE); // un-minimize first
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) return;

        int relX = (((r.left - vx) % sw) + sw) % sw; // sub-cell position
        int relY = (((r.top - vy) % sh) + sh) % sh;
        long offViewX = _maxX - (long)col * sw;
        long offViewY = _maxY - (long)row * sh;
        int newLeft = (int)(vx + relX + (_offX - offViewX));
        int newTop = (int)(vy + relY + (_offY - offViewY));
        Native.SetWindowPos(hwnd, IntPtr.Zero, newLeft, newTop, 0, 0, MoveFlags);

        // If it landed on the cell we're currently looking at, bring it to front.
        int viewCol = (int)Math.Round((_maxX - (double)_offX) / sw);
        int viewRow = (int)Math.Round((_maxY - (double)_offY) / sh);
        if (col == viewCol && row == viewRow) ForceForeground(hwnd);
    }

    /// <summary>
    /// Build a small thumbnail of what cell (col,row) contains, by rendering each
    /// window on it with PrintWindow and compositing at its cell-relative position.
    /// Works for off-screen cells too. Returns null if nothing renders.
    /// </summary>
    public Bitmap? CaptureCellPreview(int col, int row, int maxW, int maxH)
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) return null;

        // How much windows would shift on screen if we panned to view this cell.
        long shiftX = (_maxX - (long)col * sw) - _offX;
        long shiftY = (_maxY - (long)row * sh) - _offY;

        float scale = Math.Min(maxW / (float)sw, maxH / (float)sh);
        int tw = Math.Max(1, (int)(sw * scale));
        int th = Math.Max(1, (int)(sh * scale));

        var thumb = new Bitmap(tw, th);
        bool any = false;
        using (var g = Graphics.FromImage(thumb))
        {
            g.Clear(Color.FromArgb(18, 22, 44));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;

            // EnumWindows is top→bottom; reverse so the topmost window is painted last.
            var sb = new StringBuilder(64);
            var list = new List<IntPtr>();
            Native.EnumWindows((h, _) => { if (IsMovable(h, sb)) list.Add(h); return true; }, IntPtr.Zero);
            list.Reverse();

            foreach (var h in list)
            {
                if (!Native.GetWindowRect(h, out var r)) continue;
                int ww = r.right - r.left, wh = r.bottom - r.top;
                if (ww <= 0 || wh <= 0) continue;

                float lx = r.left + shiftX - vx;   // window pos within the cell screen
                float ly = r.top + shiftY - vy;
                if (lx + ww <= 0 || ly + wh <= 0 || lx >= sw || ly >= sh) continue; // outside cell

                using var temp = new Bitmap(ww, wh);
                bool ok;
                using (var tg = Graphics.FromImage(temp))
                {
                    var hdc = tg.GetHdc();
                    ok = Native.PrintWindow(h, hdc, Native.PW_RENDERFULLCONTENT);
                    tg.ReleaseHdc(hdc);
                }
                if (!ok) continue;
                g.DrawImage(temp, lx * scale, ly * scale, ww * scale, wh * scale);
                any = true;
            }
        }

        if (!any) { thumb.Dispose(); return null; }
        return thumb;
    }

    /// <summary>
    /// Place a single window onto grid cell (col,row), keeping the screen-relative
    /// position it had at <paramref name="origLeft"/>/<paramref name="origTop"/>.
    /// Does not change the global pan offset — only this one window moves.
    /// </summary>
    public void MoveWindowToCell(IntPtr hwnd, int col, int row, int origLeft, int origTop)
    {
        if (hwnd == IntPtr.Zero) return;
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        // offset at which cell (col,row) is shown: leftmost cell at +maxX, rightmost at -maxX
        long offViewX = _maxX - (long)col * sw;
        long offViewY = _maxY - (long)row * sh;
        int dx = (int)(_offX - offViewX);
        int dy = (int)(_offY - offViewY);
        Native.SetWindowPos(hwnd, IntPtr.Zero, origLeft + dx, origTop + dy, 0, 0, MoveFlags);
    }

    public void EndGesture()
    {
        _gesture.Clear();
        _gesturePos.Clear();
    }

    /// <summary>
    /// The single "reset" entry point used everywhere (startup, exit, tray menu,
    /// settings button): undo every desktop's pan, then rescue any off-screen window.
    /// </summary>
    public void Reset(bool allDesktops = false)
    {
        RestoreHome();
        SnapOffScreenWindows(allDesktops);
    }

    /// <summary>
    /// Restores windows to their home positions on EVERY virtual desktop by undoing
    /// each desktop's accumulated pan offset. Independent of _origin — always works.
    /// </summary>
    private void RestoreHome()
    {
        // Per-desktop offset map; the current desktop uses the live offset.
        var offsets = new Dictionary<Guid, (long x, long y)>(_desktopOffsets);
        var current = CurrentDesktopId();
        if (current != Guid.Empty)
            offsets[current] = (_offX, _offY);

        var sb = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            if (!IsMovable(h, sb, anyDesktop: true)) return true;

            // Which desktop does this window live on → which offset to undo
            Guid id = Guid.Empty;
            if (_vdm is not null)
            {
                try { _vdm.GetWindowDesktopId(h, out id); } catch { }
            }
            if (id == Guid.Empty)
                id = current; // no VDM / unknown → assume current desktop

            if (!offsets.TryGetValue(id, out var off)) return true;
            if (off.x == 0 && off.y == 0) return true;
            if (Native.GetWindowRect(h, out var r))
                Native.SetWindowPos(h, IntPtr.Zero, (int)(r.left - off.x), (int)(r.top - off.y), 0, 0, MoveFlags);
            return true;
        }, IntPtr.Zero);

        _desktopOffsets.Clear();
        _origin.Clear();
        _gesture.Clear();
        _gesturePos.Clear();
        _offX = _offY = 0;
    }

    /// <summary>
    /// Called when the Windows virtual desktop is switched.
    /// Saves the canvas position for the old desktop and restores it for the new one.
    /// </summary>
    public void OnDesktopSwitch(Guid oldId, Guid newId)
    {
        // End any active gesture
        if (_gesture.Count > 0) EndGesture();

        // Save the current offset for the old desktop
        if (oldId != Guid.Empty)
            _desktopOffsets[oldId] = (_offX, _offY);

        // Reset origin — the new desktop has different HWNDs
        _origin.Clear();

        // Restore the offset for the new desktop (or 0,0 if it's the first time)
        if (_desktopOffsets.TryGetValue(newId, out var saved))
            (_offX, _offY) = saved;
        else
            (_offX, _offY) = (0, 0);
    }

    /// <summary>
    /// Safety net: if any window ended up outside the visible area, quietly bring it back.
    /// </summary>
    private void SnapOffScreenWindows(bool allDesktops = false)
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        bool OffScreen(Native.RECT r) =>
            r.right < vx + 50 || r.bottom < vy + 50 || r.left > vx + vw - 50 || r.top > vy + vh - 50;

        var sb = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            if (!IsMovable(h, sb, anyDesktop: allDesktops, includeMinimized: true)) return true;

            // Minimized: fix the restore rectangle so it doesn't reappear off-screen.
            if (Native.IsIconic(h))
            {
                var wp = new Native.WINDOWPLACEMENT { length = 44 };
                if (Native.GetWindowPlacement(h, ref wp) && OffScreen(wp.rcNormalPosition))
                {
                    var n = wp.rcNormalPosition;
                    int w = n.right - n.left, ht = n.bottom - n.top;
                    wp.rcNormalPosition = new Native.RECT { left = vx + 80, top = vy + 80, right = vx + 80 + w, bottom = vy + 80 + ht };
                    Native.SetWindowPlacement(h, ref wp);
                }
                return true;
            }

            if (!Native.GetWindowRect(h, out var r)) return true;
            if (OffScreen(r))
                Native.SetWindowPos(h, IntPtr.Zero, vx + 80, vy + 80, 0, 0, MoveFlags);
            return true;
        }, IntPtr.Zero);
    }

    private void MoveBy(int dx, int dy)
    {
        if (_gesture.Count == 0) return;

        // Move top to bottom (EnumWindows order: [0] = topmost).
        // The topmost window moves first — there is no "hole" beneath it yet, so DWM
        // doesn't get a chance to show an intermediate frame with wrong overlap.
        foreach (var h in _gesture)
        {
            if (!_gesturePos.TryGetValue(h, out var pos)) continue;
            int nx = pos.x + dx;
            int ny = pos.y + dy;
            _gesturePos[h] = (nx, ny);
            Native.SetWindowPos(h, IntPtr.Zero, nx, ny, 0, 0, MoveFlags);
        }
    }

    private bool IsMovable(IntPtr h, StringBuilder sb, bool anyDesktop = false, bool includeMinimized = false)
    {
        if (!Native.IsWindowVisible(h)) return false;
        if (!includeMinimized && Native.IsIconic(h)) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;

        sb.Clear();
        Native.GetClassName(h, sb, sb.Capacity);
        switch (sb.ToString())
        {
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "Progman":
            case "WorkerW":
            case "Windows.UI.Core.CoreWindow":
            case "XamlExplorerHostIslandWindow":
                return false;
        }

        // For a full reset we move windows on every virtual desktop, so skip this check.
        if (!anyDesktop && _vdm is not null)
        {
            try
            {
                if (_vdm.IsWindowOnCurrentVirtualDesktop(h, out var on) == 0 && on == 0)
                    return false;
            }
            catch { }
        }
        return true;
    }
}
