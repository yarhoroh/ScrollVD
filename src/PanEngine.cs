using System.Text;

namespace ScrollVD;

internal sealed class PanEngine
{
    // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER
    private const uint MoveFlags =
        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;

    private readonly Native.IVirtualDesktopManager? _vdm;

    // Список окон в порядке EnumWindows (TOP → BOTTOM, т.е. [0] = самое верхнее)
    private readonly List<IntPtr> _gesture = new();

    // Текущие позиции — обновляются инкрементально, без GetWindowRect на каждом кадре
    private readonly Dictionary<IntPtr, (int x, int y)> _gesturePos = new();

    // Исходные позиции до первого сдвига — для точного Reset()
    private readonly Dictionary<IntPtr, (int x, int y)> _origin = new();

    private long _offX, _offY;
    private int _maxX, _maxY;

    // Сохранённые позиции холста для каждого виртуального рабочего стола
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
        // Быстрый путь: foreground-окно
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && _vdm.GetWindowDesktopId(fg, out var id) == 0 && id != Guid.Empty)
                return id;
        }
        catch { }
        // Fallback: ищем первое видимое обычное окно, у которого есть desktop ID
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
    /// Начало жеста: перечисляем окна и запоминаем их текущие позиции.
    /// GetWindowRect вызывается только здесь — один раз за жест.
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
    public void Reset()
    {
        RestoreHome();
        SnapOffScreenWindows();
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
    /// Вызывается при переключении виртуального рабочего стола Windows.
    /// Сохраняет позицию холста для старого стола, восстанавливает для нового.
    /// </summary>
    public void OnDesktopSwitch(Guid oldId, Guid newId)
    {
        // Завершаем любой активный жест
        if (_gesture.Count > 0) EndGesture();

        // Сохраняем текущее смещение для старого стола
        if (oldId != Guid.Empty)
            _desktopOffsets[oldId] = (_offX, _offY);

        // Сбрасываем origin — на новом столе HWND другие
        _origin.Clear();

        // Восстанавливаем смещение для нового стола (или 0,0 если первый раз)
        if (_desktopOffsets.TryGetValue(newId, out var saved))
            (_offX, _offY) = saved;
        else
            (_offX, _offY) = (0, 0);
    }

    /// <summary>
    /// Safety net: if any window ended up outside the visible area, quietly bring it back.
    /// </summary>
    private void SnapOffScreenWindows()
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        var sb = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            if (!IsMovable(h, sb)) return true;
            if (!Native.GetWindowRect(h, out var r)) return true;

            bool offScreen = r.right  < vx + 50
                          || r.bottom < vy + 50
                          || r.left   > vx + vw - 50
                          || r.top    > vy + vh - 50;
            if (offScreen)
                Native.SetWindowPos(h, IntPtr.Zero, vx + 80, vy + 80, 0, 0, MoveFlags);
            return true;
        }, IntPtr.Zero);
    }

    private void MoveBy(int dx, int dy)
    {
        if (_gesture.Count == 0) return;

        // Двигаем сверху вниз (порядок EnumWindows: [0] = topmost).
        // Верхнее окно перемещается первым — под ним ещё нет «дыры», DWM
        // не успевает показать промежуточный кадр с неправильным перекрытием.
        foreach (var h in _gesture)
        {
            if (!_gesturePos.TryGetValue(h, out var pos)) continue;
            int nx = pos.x + dx;
            int ny = pos.y + dy;
            _gesturePos[h] = (nx, ny);
            Native.SetWindowPos(h, IntPtr.Zero, nx, ny, 0, 0, MoveFlags);
        }
    }

    private bool IsMovable(IntPtr h, StringBuilder sb, bool anyDesktop = false)
    {
        if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
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
