using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ScrollVD;

/// <summary>
/// Minimap — a translucent borderless window on top of all windows.
/// Shows the entire virtual canvas; the rectangle = current viewport.
///
/// Controls:
///   Click              → move the viewport to this point on the canvas
///   Ctrl + drag        → move the minimap window itself
///   Drag the bottom-right corner (12 px) → resize the window
///   Double-tap MinimapHotkey → show / hide
/// </summary>
internal sealed class MinimapForm : Form
{
    private readonly PanEngine _engine;

    // --- window drag state ---
    private bool _winDrag;
    private Point _winDragCursor;
    private Point _winDragOrigin;

    // --- viewport rectangle drag state ---
    private bool _vpDrag;
    private Point _vpDragStart;
    private (long offX, long offY) _vpDragOrigin;

    // --- resize state ---
    private bool _resizing;
    private Point _resizeCursor;
    private Size _resizeOriginSize;

    private const int ResizeGrip = 14; // px of the resize zone in the bottom-right corner
    private const int BorderR = 8;     // corner rounding

    // --- active window marker (red dot for a couple of seconds) ---
    private long _markerX, _markerY;   // position in canvas coordinates
    private long _markerUntil;         // TickCount64 until which the marker is drawn

    // --- highlighted cell while dragging a window onto the minimap (-1 = none) ---
    private int _hlCol = -1, _hlRow = -1;

    // --- hover preview popup of a cell's contents ---
    private readonly PreviewForm _preview = new();
    private int _prevCol = -1, _prevRow = -1;

    // --- right-click cell popup: list of windows to drop into a cell ---
    private CellListPopup? _cellPopup;

    // --- occupied-cell hint: app icons per cell (throttled recompute, icons cached) ---
    private Dictionary<(int col, int row), List<IntPtr>> _cellIcons = new();
    private readonly Dictionary<IntPtr, Bitmap> _iconCache = new();
    private long _occupiedAt;

    // --- current Windows virtual desktop number, shown in the centre (throttled) ---
    private int _deskNum;
    private long _deskNumAt;

    // WS_EX_NOACTIVATE — clicks don't switch focus
    private const int WsExNoActivate = 0x08000000;
    // WS_EX_TOOLWINDOW — doesn't appear in Alt+Tab
    private const int WsExToolWindow = 0x00000080;

    public MinimapForm(PanEngine engine)
    {
        _engine = engine;

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        Opacity = 0.88;
        BackColor = Color.FromArgb(14, 18, 38);
        ShowInTaskbar = false;
        DoubleBuffered = true;

        // Size
        int w = Config.Current.MinimapWidth;
        int h = Config.Current.MinimapHeight > 0 ? Config.Current.MinimapHeight : CalcAutoHeight(w);
        Size = new Size(w, h);

        // Position: saved or automatic (top-right corner of the primary monitor)
        StartPosition = FormStartPosition.Manual;
        if (Config.Current.MinimapX >= 0 && Config.Current.MinimapY >= 0)
            Location = new Point(Config.Current.MinimapX, Config.Current.MinimapY);
        else
            PlaceAutoTopRight();

        // Form icon
        try
        {
            using var s = typeof(MinimapForm).Assembly.GetManifestResourceStream("ScrollVD.icon.ico");
            if (s is not null) Icon = new Icon(s);
        }
        catch { }

        // Repaint timer (~30 fps is enough for the minimap)
        var timer = new System.Windows.Forms.Timer { Interval = 33 };
        timer.Tick += (_, _) => Invalidate();
        timer.Start();
        FormClosed += (_, _) =>
        {
            timer.Stop();
            _preview.Dispose();
            _cellPopup?.Dispose();
            foreach (var b in _iconCache.Values) b.Dispose();
        };

        // On the close button / Alt+F4 we hide rather than close. But on application exit
        // (Application.Exit) we skip the cancel, otherwise the process hangs in the tray.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    // WM_MOUSEACTIVATE → MA_NOACTIVATE: clicks don't switch focus even without WS_EX_NOACTIVATE
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate) { m.Result = (IntPtr)MaNoActivate; return; }
        base.WndProc(ref m);
    }

    // ====== Rendering ======
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int w = ClientSize.Width, h = ClientSize.Height;

        // Background with rounded corners
        using var bgPath = RoundRect(0, 0, w, h, BorderR);
        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, w, h),
            Color.FromArgb(20, 25, 52),
            Color.FromArgb(12, 16, 36),
            90f);
        g.FillPath(bgBrush, bgPath);

        // Thin accent border
        using var borderPen = new Pen(Color.FromArgb(70, 100, 200), 1.2f);
        g.DrawPath(borderPen, bgPath);

        var (offX, offY, maxX, maxY) = _engine.GetState();
        var (cols, rows, screenW, screenH) = _engine.GridInfo();
        if (screenW <= 0 || screenH <= 0) return;

        int pad = 6;
        int mw = w - pad * 2, mh = h - pad * 2;

        // The whole grid spans cols×rows cells; the viewport is one cell.
        float totalCW = (float)cols * screenW;
        float totalCH = (float)rows * screenH;
        float sx = mw / totalCW;
        float sy = mh / totalCH;

        // Cell boundary lines
        using var gridPen = new Pen(Color.FromArgb(40, 80, 110, 170), 0.6f);
        for (int i = 1; i < cols; i++)
        {
            float gx = pad + mw * i / cols;
            g.DrawLine(gridPen, gx, pad, gx, pad + mh);
        }
        for (int i = 1; i < rows; i++)
        {
            float gy = pad + mh * i / rows;
            g.DrawLine(gridPen, pad, gy, pad + mw, gy);
        }

        // Cells that contain windows: faint tint + the app icons in them
        // (recomputed ~every 300 ms; the icons themselves are cached).
        if (Environment.TickCount64 - _occupiedAt > 300)
        {
            _cellIcons = _engine.CellIcons();
            _occupiedAt = Environment.TickCount64;
        }
        if (_cellIcons.Count > 0)
        {
            var (ocols, orows, ocw, och, opad) = CellLayout();
            using var occFill = new SolidBrush(Color.FromArgb(38, 120, 180, 255));
            foreach (var kv in _cellIcons)
            {
                var (c, r) = kv.Key;
                if (c < ocols && r < orows)
                    g.FillRectangle(occFill, opad + c * ocw, opad + r * och, ocw, och);
            }
        }

        // Highlighted target cell while a window is dragged onto the minimap
        if (_hlCol >= 0 && _hlRow >= 0)
        {
            var (hcols, hrows, cw, ch, padc) = CellLayout();
            if (_hlCol < hcols && _hlRow < hrows)
            {
                var hr = new RectangleF(padc + _hlCol * cw, padc + _hlRow * ch, cw, ch);
                using var hlFill = new SolidBrush(Color.FromArgb(90, 120, 220, 130));
                using var hlPen = new Pen(Color.FromArgb(230, 150, 240, 160), 1.8f);
                g.FillRectangle(hlFill, hr);
                g.DrawRectangle(hlPen, hr.X, hr.Y, hr.Width, hr.Height);
            }
        }

        // Viewport: viewport left = -offX in the canvas coordinate space
        float vpLeft = pad + (-offX + maxX) * sx;
        float vpTop = pad + (-offY + maxY) * sy;
        float vpW = screenW * sx;
        float vpH = screenH * sy;
        var vpRect = new RectangleF(vpLeft, vpTop, vpW, vpH);

        // Fill
        using var vpFill = new SolidBrush(Color.FromArgb(55, 110, 180, 255));
        g.FillRectangle(vpFill, vpRect);

        // Viewport border
        using var vpPen = new Pen(Color.FromArgb(220, 140, 190, 255), 1.5f);
        g.DrawRectangle(vpPen, vpRect.X, vpRect.Y, vpRect.Width, vpRect.Height);

        // Current Windows virtual desktop number — shown only when there's more than one
        if (Environment.TickCount64 - _deskNumAt > 700)
        {
            _deskNum = _engine.VirtualDesktopCount() > 1 ? _engine.CurrentDesktopNumber() : 0;
            _deskNumAt = Environment.TickCount64;
        }
        if (_deskNum > 0)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            float fs = mh * 0.55f / 1.7f;
            using var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var rect = new RectangleF(pad, pad, mw, mh);
            using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            using var fore = new SolidBrush(Color.FromArgb(70, 200, 220, 255));
            g.DrawString(_deskNum.ToString(), f, shadow, new RectangleF(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), sf);
            g.DrawString(_deskNum.ToString(), f, fore, rect, sf);
        }

        // App icons per cell — drawn on top of the viewport & number so they stay visible
        if (_cellIcons.Count > 0)
        {
            var (icols, irows, icw, ich, ipad) = CellLayout();
            foreach (var kv in _cellIcons)
            {
                var (c, r) = kv.Key;
                if (c < icols && r < irows)
                    DrawCellIcons(g, new RectangleF(ipad + c * icw, ipad + r * ich, icw, ich), kv.Value);
            }
        }

        // Active window marker — a translucent red dot for a couple of seconds
        if (Environment.TickCount64 < _markerUntil)
        {
            float dotX = pad + (_markerX + maxX) * sx;
            float dotY = pad + (_markerY + maxY) * sy;
            dotX = Math.Clamp(dotX, pad, pad + mw);
            dotY = Math.Clamp(dotY, pad, pad + mh);
            float dr = 7f / 3f;
            using var halo = new SolidBrush(Color.FromArgb(70, 255, 60, 60));
            using var core = new SolidBrush(Color.FromArgb(200, 255, 40, 40));
            g.FillEllipse(halo, dotX - dr * 2, dotY - dr * 2, dr * 4, dr * 4);
            g.FillEllipse(core, dotX - dr, dotY - dr, dr * 2, dr * 2);
        }

        // Resize grip (bottom-right corner)
        using var gripPen = new Pen(Color.FromArgb(100, 120, 160), 1f);
        for (int i = 2; i <= 4; i++)
        {
            int d = i * 3;
            g.DrawLine(gripPen, w - 2, h - 2 - d, w - 2 - d, h - 2);
        }
    }

    // Draw up to 6 distinct app icons in a grid that fills the cell; icons grow when
    // there are few and shrink when there are many. "+N" takes a slot if there are more.
    private void DrawCellIcons(Graphics g, RectangleF cell, List<IntPtr> icons)
    {
        if (icons.Count == 0) return;
        const int cap = 6;
        bool more = icons.Count > cap;
        int realIcons = more ? cap - 1 : Math.Min(icons.Count, cap); // keep a slot for "+N"
        int slots = realIcons + (more ? 1 : 0);

        int cols = (int)Math.Ceiling(Math.Sqrt(slots));
        int rows = (int)Math.Ceiling(slots / (double)cols);

        const float gap = 4f, inset = 3f;
        float availW = cell.Width - inset * 2, availH = cell.Height - inset * 2;
        float size = Math.Min((availW - (cols - 1) * gap) / cols, (availH - (rows - 1) * gap) / rows);
        size = Math.Clamp(size, 7f, 28f);

        float gridW = cols * size + (cols - 1) * gap;
        float gridH = rows * size + (rows - 1) * gap;
        float startX = cell.X + (cell.Width - gridW) / 2f;
        float startY = cell.Y + (cell.Height - gridH) / 2f;

        var im = g.InterpolationMode;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        for (int i = 0; i < slots; i++)
        {
            float x = startX + (i % cols) * (size + gap);
            float y = startY + (i / cols) * (size + gap);
            if (i < realIcons)
            {
                var bmp = GetIconBitmap(icons[i]);
                if (bmp is not null) g.DrawImage(bmp, x, y, size, size);
            }
            else
            {
                using var f = new Font("Segoe UI", size * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var br = new SolidBrush(Color.FromArgb(235, 220, 230, 255));
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("+" + (icons.Count - realIcons), f, br, new RectangleF(x, y, size, size), sf);
            }
        }
        g.InterpolationMode = im;
    }

    // HICON → Bitmap, cached (the HICON belongs to the window; we never destroy it).
    private Bitmap? GetIconBitmap(IntPtr hIcon)
    {
        if (_iconCache.TryGetValue(hIcon, out var cached)) return cached;
        Bitmap? bmp = null;
        try { using var ic = Icon.FromHandle(hIcon); bmp = ic.ToBitmap(); }
        catch { bmp = null; }
        if (bmp is not null) _iconCache[hIcon] = bmp;
        return bmp;
    }

    /// <summary>Re-assert the always-on-top status (other windows can steal it).</summary>
    public void ReassertTopMost()
    {
        if (!Visible) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Show a red marker at the canvas point (canvasX, canvasY) for 2 seconds.</summary>
    public void ShowMarker(long canvasX, long canvasY)
    {
        _markerX = canvasX;
        _markerY = canvasY;
        _markerUntil = Environment.TickCount64 + 1000;
        Invalidate();
    }

    // Grid layout: number of cells per axis and cell size in client px.
    private (int cols, int rows, float cw, float ch, int pad) CellLayout()
    {
        var (cols, rows, _, _) = _engine.GridInfo();
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        int pad = 6;
        float cw = (ClientSize.Width - pad * 2) / (float)cols;
        float ch = (ClientSize.Height - pad * 2) / (float)rows;
        return (cols, rows, cw, ch, pad);
    }

    /// <summary>Hit-test a screen point against the 3x3 (or NxN) cell grid.</summary>
    public bool TryGetCellAtScreen(Point screenPt, out int col, out int row)
    {
        col = row = -1;
        if (!Visible) return false;
        var c = PointToClient(screenPt);
        var (cols, rows, cw, ch, pad) = CellLayout();
        if (cw <= 0 || ch <= 0) return false;
        if (c.X < pad || c.Y < pad || c.X >= pad + cw * cols || c.Y >= pad + ch * rows) return false;
        col = Math.Clamp((int)((c.X - pad) / cw), 0, cols - 1);
        row = Math.Clamp((int)((c.Y - pad) / ch), 0, rows - 1);
        return true;
    }

    /// <summary>Highlight a cell (or pass -1,-1 to clear). Repaints only on change.</summary>
    public void SetHighlight(int col, int row)
    {
        if (col == _hlCol && row == _hlRow) return;
        _hlCol = col;
        _hlRow = row;
        Invalidate();
    }

    private static GraphicsPath RoundRect(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ====== Helper methods ======

    /// <summary>Computes the viewport rectangle in client-area coordinates.</summary>
    private RectangleF GetViewportRect()
    {
        var (offX, offY, maxX, maxY) = _engine.GetState();
        var (cols, rows, screenW, screenH) = _engine.GridInfo();
        if (screenW <= 0 || screenH <= 0) return RectangleF.Empty;
        int pad = 6;
        float sx = (ClientSize.Width - pad * 2) / ((float)cols * screenW);
        float sy = (ClientSize.Height - pad * 2) / ((float)rows * screenH);
        return new RectangleF(
            pad + (-offX + maxX) * sx,
            pad + (-offY + maxY) * sy,
            screenW * sx,
            screenH * sy);
    }

    // ====== Interaction ======
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        _prevCol = _prevRow = -1;
        _preview.HidePreview();

        bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;

        if (ctrl)
        {
            _winDrag = true;
            _winDragCursor = Cursor.Position;
            _winDragOrigin = Location;
        }
        else if (InResizeZone(e.Location))
        {
            _resizing = true;
            _resizeCursor = Cursor.Position;
            _resizeOriginSize = Size;
        }
        else if (GetViewportRect().Contains(e.X, e.Y))
        {
            // Pressed on the viewport rectangle → drag it
            _vpDrag = true;
            _vpDragStart = e.Location;
            var (offX, offY, _, _) = _engine.GetState();
            _vpDragOrigin = (offX, offY);
            _engine.BeginGesture();
            Cursor = Cursors.SizeAll;
        }
        // Otherwise — click outside the VP → jump, handled in OnMouseUp
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_winDrag)
        {
            var d = Cursor.Position;
            Location = new Point(
                _winDragOrigin.X + d.X - _winDragCursor.X,
                _winDragOrigin.Y + d.Y - _winDragCursor.Y);
        }
        else if (_resizing)
        {
            var d = Cursor.Position;
            int newW = Math.Max(120, _resizeOriginSize.Width + (d.X - _resizeCursor.X));
            int newH = Math.Max(80, _resizeOriginSize.Height + (d.Y - _resizeCursor.Y));
            Size = new Size(newW, newH);
            Invalidate();
        }
        else if (_vpDrag)
        {
            var (cols, rows, screenW2, screenH2) = _engine.GridInfo();
            if (screenW2 <= 0 || screenH2 <= 0) return;
            int pad = 6;
            float sx = (ClientSize.Width - pad * 2) / ((float)cols * screenW2);
            float sy = (ClientSize.Height - pad * 2) / ((float)rows * screenH2);

            // Delta in minimap px → delta in canvas units
            int dx = e.X - _vpDragStart.X;
            int dy = e.Y - _vpDragStart.Y;

            long targetOffX = _vpDragOrigin.offX - (long)(dx / sx);
            long targetOffY = _vpDragOrigin.offY - (long)(dy / sy);

            if (Config.Current.SnapMode)
            {
                targetOffX = (long)Math.Round((double)targetOffX / screenW2) * screenW2;
                targetOffY = (long)Math.Round((double)targetOffY / screenH2) * screenH2;
            }

            _engine.JumpTo(targetOffX, targetOffY);
        }
        else
        {
            // Cursor hint based on the zone
            if (InResizeZone(e.Location))
                Cursor = Cursors.SizeNWSE;
            else if (GetViewportRect().Contains(e.X, e.Y))
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Default;

            UpdateHoverPreview(e.Location);
        }
    }

    // Show/refresh a thumbnail of the cell under the cursor (only when it changes).
    private void UpdateHoverPreview(Point clientPt)
    {
        if (!TryGetCellAtScreen(PointToScreen(clientPt), out int col, out int row))
        {
            _prevCol = _prevRow = -1;
            _preview.HidePreview();
            return;
        }
        if (col == _prevCol && row == _prevRow) return; // same cell — keep current preview
        _prevCol = col;
        _prevRow = row;

        var bmp = _engine.CaptureCellPreview(col, row, 187, 120);
        if (bmp is null) { _preview.HidePreview(); return; }

        // Place to the left of the minimap; fall back to the right if no room.
        int px = Bounds.Left - bmp.Width - 14;
        if (px < 0) px = Bounds.Right + 8;
        _preview.ShowAt(bmp, new Point(px, Bounds.Top));
    }

    // Popup list of windows to drop into cell (col,row) — a lightweight no-activate
    // topmost window, shown the same way as the hover preview.
    private void ShowCellMenu(int col, int row)
    {
        if (_cellPopup is { IsDisposed: false }) _cellPopup.Close();
        _cellPopup = new CellListPopup(_engine, col, row, Cursor.Position);
        _cellPopup.Show();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _prevCol = _prevRow = -1;
        _preview.HidePreview();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) ReassertTopMost();
        else { _prevCol = _prevRow = -1; _preview.HidePreview(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        // Right-click a cell → standard Windows menu of windows to move into that cell
        if (e.Button == MouseButtons.Right)
        {
            if (TryGetCellAtScreen(PointToScreen(e.Location), out int col, out int row))
            {
                _preview.HidePreview();
                _prevCol = _prevRow = -1;
                ShowCellMenu(col, row);
            }
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        bool wasDragging = _winDrag || _resizing || _vpDrag;

        if (_winDrag) { _winDrag = false; SaveGeometry(); }
        if (_resizing) { _resizing = false; SaveGeometry(); }
        if (_vpDrag) { _vpDrag = false; _engine.EndGesture(); Cursor = Cursors.Default; }

        // Click without drag → viewport jump
        if (!wasDragging)
            JumpViewport(e.Location);
    }

    private void JumpViewport(Point miniPt)
    {
        var (_, _, maxX, maxY) = _engine.GetState();
        var (cols, rows, screenW, screenH) = _engine.GridInfo();
        if (screenW <= 0 || screenH <= 0) return;

        int pad = 6;
        int mw = ClientSize.Width - pad * 2;
        int mh = ClientSize.Height - pad * 2;

        // Click point → canvas coordinate
        float cx = (miniPt.X - pad) / (float)mw * (cols * screenW) - maxX;
        float cy = (miniPt.Y - pad) / (float)mh * (rows * screenH) - maxY;

        // Center the viewport on this point: offX = -(cx - screenW/2)
        long targetOffX = -(long)(cx - screenW / 2f);
        long targetOffY = -(long)(cy - screenH / 2f);

        if (Config.Current.SnapMode)
        {
            targetOffX = (long)Math.Round((double)targetOffX / screenW) * screenW;
            targetOffY = (long)Math.Round((double)targetOffY / screenH) * screenH;
        }

        _engine.BeginGesture();
        _engine.JumpTo(targetOffX, targetOffY);
        _engine.EndGesture();
    }

    private bool InResizeZone(Point p)
        => p.X >= ClientSize.Width - ResizeGrip && p.Y >= ClientSize.Height - ResizeGrip;

    private void SaveGeometry()
    {
        Config.Current.MinimapX = Location.X;
        Config.Current.MinimapY = Location.Y;
        Config.Current.MinimapWidth = Width;
        Config.Current.MinimapHeight = Height;
        Config.Current.Save();
    }

    private void PlaceAutoTopRight()
    {
        var scr = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(scr.Right - Width - 16, scr.Top + 16);
    }

    private static int CalcAutoHeight(int w)
    {
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        return sw > 0 ? w * sh / sw : w * 9 / 16;
    }

    public void ToggleVisible()
    {
        if (Visible) { Hide(); Config.Current.MinimapVisible = false; }
        else { Show(); Config.Current.MinimapVisible = true; }
        Config.Current.Save();
    }
}
