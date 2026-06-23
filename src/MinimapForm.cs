using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ScrollVD;

/// <summary>
/// Миникарта — полупрозрачное безрамочное окно поверх всех окон.
/// Показывает весь виртуальный холст; прямоугольник = текущая область видимости.
///
/// Управление:
///   Клик               → переместить область видимости на эту точку холста
///   Ctrl + drag        → переместить само окно миникарты
///   Drag за правый нижний угол (12 px) → изменить размер окна
///   Двойной тап MinimapHotkey → показать / скрыть
/// </summary>
internal sealed class MinimapForm : Form
{
    private readonly PanEngine _engine;

    // --- состояние перетаскивания окна ---
    private bool _winDrag;
    private Point _winDragCursor;
    private Point _winDragOrigin;

    // --- состояние перетаскивания прямоугольника вьюпорта ---
    private bool _vpDrag;
    private Point _vpDragStart;
    private (long offX, long offY) _vpDragOrigin;

    // --- состояние ресайза ---
    private bool _resizing;
    private Point _resizeCursor;
    private Size _resizeOriginSize;

    private const int ResizeGrip = 14; // px зоны ресайза в правом нижнем углу
    private const int BorderR = 8;     // скругление

    // --- метка активного окна (красная точка на пару секунд) ---
    private long _markerX, _markerY;   // позиция в координатах холста
    private long _markerUntil;         // TickCount64, до которого метку рисуем

    // WS_EX_NOACTIVATE — клики не переключают фокус
    private const int WsExNoActivate = 0x08000000;
    // WS_EX_TOOLWINDOW — не попадает в Alt+Tab
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

        // Размер
        int w = Config.Current.MinimapWidth;
        int h = Config.Current.MinimapHeight > 0 ? Config.Current.MinimapHeight : CalcAutoHeight(w);
        Size = new Size(w, h);

        // Позиция: сохранённая или авто (правый верхний угол основного монитора)
        StartPosition = FormStartPosition.Manual;
        if (Config.Current.MinimapX >= 0 && Config.Current.MinimapY >= 0)
            Location = new Point(Config.Current.MinimapX, Config.Current.MinimapY);
        else
            PlaceAutoTopRight();

        // Иконка формы
        try
        {
            using var s = typeof(MinimapForm).Assembly.GetManifestResourceStream("ScrollVD.icon.ico");
            if (s is not null) Icon = new Icon(s);
        }
        catch { }

        // Таймер перерисовки (~30 fps достаточно для миникарты)
        var timer = new System.Windows.Forms.Timer { Interval = 33 };
        timer.Tick += (_, _) => Invalidate();
        timer.Start();
        FormClosed += (_, _) => timer.Stop();

        // По «крестику»/Alt+F4 не закрываем, а прячем. Но при выходе из приложения
        // (Application.Exit) закрытие пропускаем, иначе процесс зависнет в трее.
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

    // WM_MOUSEACTIVATE → MA_NOACTIVATE: клики не переключают фокус даже без WS_EX_NOACTIVATE
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate) { m.Result = (IntPtr)MaNoActivate; return; }
        base.WndProc(ref m);
    }

    // ====== Отрисовка ======
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int w = ClientSize.Width, h = ClientSize.Height;

        // Фон с закруглёнными краями
        using var bgPath = RoundRect(0, 0, w, h, BorderR);
        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, w, h),
            Color.FromArgb(20, 25, 52),
            Color.FromArgb(12, 16, 36),
            90f);
        g.FillPath(bgBrush, bgPath);

        // Тонкая акцентная рамка
        using var borderPen = new Pen(Color.FromArgb(70, 100, 200), 1.2f);
        g.DrawPath(borderPen, bgPath);

        var (offX, offY, maxX, maxY) = _engine.GetState();
        if (maxX <= 0 || maxY <= 0) return;

        int pad = 6;
        int mw = w - pad * 2, mh = h - pad * 2;

        int screenW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int screenH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        // Холст = [-maxX .. maxX], вьюпорт = screenW/screenH.
        // Полный диапазон, видимый при любом offX: от -maxX до maxX+screenW.
        // totalCW = 2*maxX + screenW гарантирует, что вьюпорт всегда внутри карты.
        float totalCW = 2f * maxX + screenW;
        float totalCH = 2f * maxY + screenH;
        float sx = mw / totalCW;
        float sy = mh / totalCH;

        // Сетка холста — лёгкие линии
        using var gridPen = new Pen(Color.FromArgb(30, 70, 100, 160), 0.5f);
        int gridSteps = 4;
        for (int i = 1; i < gridSteps; i++)
        {
            float gx = pad + mw * i / gridSteps;
            float gy = pad + mh * i / gridSteps;
            g.DrawLine(gridPen, gx, pad, gx, pad + mh);
            g.DrawLine(gridPen, pad, gy, pad + mw, gy);
        }

        // «Нулевая» точка — крест
        float ox = pad + maxX * sx;
        float oy = pad + maxY * sy;
        using var axisPen = new Pen(Color.FromArgb(50, 100, 140, 200), 0.7f);
        g.DrawLine(axisPen, ox, pad, ox, pad + mh);
        g.DrawLine(axisPen, pad, oy, pad + mw, oy);

        // Область видимости: viewport left = -offX в coord-пространстве холста
        float vpLeft = pad + (-offX + maxX) * sx;
        float vpTop = pad + (-offY + maxY) * sy;
        float vpW = screenW * sx;
        float vpH = screenH * sy;
        var vpRect = new RectangleF(vpLeft, vpTop, vpW, vpH);

        // Заливка
        using var vpFill = new SolidBrush(Color.FromArgb(55, 110, 180, 255));
        g.FillRectangle(vpFill, vpRect);

        // Рамка области видимости
        using var vpPen = new Pen(Color.FromArgb(220, 140, 190, 255), 1.5f);
        g.DrawRectangle(vpPen, vpRect.X, vpRect.Y, vpRect.Width, vpRect.Height);

        // Метка активного окна — красная полупрозрачная точка на пару секунд
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

        // Ручка ресайза (правый нижний угол)
        using var gripPen = new Pen(Color.FromArgb(100, 120, 160), 1f);
        for (int i = 2; i <= 4; i++)
        {
            int d = i * 3;
            g.DrawLine(gripPen, w - 2, h - 2 - d, w - 2 - d, h - 2);
        }
    }

    /// <summary>Показать красную метку в точке холста (canvasX, canvasY) на 2 секунды.</summary>
    public void ShowMarker(long canvasX, long canvasY)
    {
        _markerX = canvasX;
        _markerY = canvasY;
        _markerUntil = Environment.TickCount64 + 1000;
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

    // ====== Вспомогательные методы ======

    /// <summary>Вычисляет прямоугольник вьюпорта в координатах клиентской области.</summary>
    private RectangleF GetViewportRect()
    {
        var (offX, offY, maxX, maxY) = _engine.GetState();
        if (maxX <= 0 || maxY <= 0) return RectangleF.Empty;
        int pad = 6;
        int screenW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int screenH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        float sx = (ClientSize.Width - pad * 2) / (2f * maxX + screenW);
        float sy = (ClientSize.Height - pad * 2) / (2f * maxY + screenH);
        return new RectangleF(
            pad + (-offX + maxX) * sx,
            pad + (-offY + maxY) * sy,
            screenW * sx,
            screenH * sy);
    }

    // ====== Взаимодействие ======
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

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
            // Зажали на прямоугольнике вьюпорта → тащим его
            _vpDrag = true;
            _vpDragStart = e.Location;
            var (offX, offY, _, _) = _engine.GetState();
            _vpDragOrigin = (offX, offY);
            _engine.BeginGesture();
            Cursor = Cursors.SizeAll;
        }
        // Иначе — клик вне VP → прыжок, обрабатывается в OnMouseUp
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
            var (_, _, maxX, maxY) = _engine.GetState();
            if (maxX <= 0 || maxY <= 0) return;
            int pad = 6;
            int screenW2 = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int screenH2 = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            float sx = (ClientSize.Width - pad * 2) / (2f * maxX + screenW2);
            float sy = (ClientSize.Height - pad * 2) / (2f * maxY + screenH2);

            // Дельта в px миникарты → дельта в единицах холста
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
            // Подсказка курсора по зоне
            if (InResizeZone(e.Location))
                Cursor = Cursors.SizeNWSE;
            else if (GetViewportRect().Contains(e.X, e.Y))
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        bool wasDragging = _winDrag || _resizing || _vpDrag;

        if (_winDrag) { _winDrag = false; SaveGeometry(); }
        if (_resizing) { _resizing = false; SaveGeometry(); }
        if (_vpDrag) { _vpDrag = false; _engine.EndGesture(); Cursor = Cursors.Default; }

        // Клик без drag → прыжок области видимости
        if (!wasDragging)
            JumpViewport(e.Location);
    }

    private void JumpViewport(Point miniPt)
    {
        var (_, _, maxX, maxY) = _engine.GetState();
        if (maxX <= 0 || maxY <= 0) return;

        int screenW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int screenH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        int pad = 6;
        int mw = ClientSize.Width - pad * 2;
        int mh = ClientSize.Height - pad * 2;

        // Точка клика → координата холста
        float cx = (miniPt.X - pad) / (float)mw * (2 * maxX + screenW) - maxX;
        float cy = (miniPt.Y - pad) / (float)mh * (2 * maxY + screenH) - maxY;

        // Центрируем viewport на этой точке: offX = -(cx - screenW/2)
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
