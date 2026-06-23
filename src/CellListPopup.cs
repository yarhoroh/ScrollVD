namespace ScrollVD;

/// <summary>
/// Lightweight popup (like <see cref="PreviewForm"/>): a top-most, no-activate window
/// listing the windows on the current virtual desktop. Picking one moves it into the
/// cell that was right-clicked. Closes on selection or on a click outside.
/// </summary>
internal sealed class CellListPopup : Form
{
    private readonly PanEngine _engine;
    private readonly int _col, _row;
    private readonly List<(IntPtr hwnd, string title)> _items;
    private int _hover = -1;

    private const int ItemH = 24;
    private const int PadX = 12;
    private const int MaxW = 380;
    private const int MinW = 160;

    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    public CellListPopup(PanEngine engine, int col, int row, Point screenLocation)
    {
        _engine = engine;
        _col = col;
        _row = row;
        _items = engine.ListMovableWindows();

        Text = ""; // empty title → ignored by our foreground/marker logic
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Opacity = 0.92;
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(20, 25, 52); // dark, like the minimap

        int rows = Math.Max(1, _items.Count);
        Size = new Size(MeasureWidth(), rows * ItemH + 2);
        Location = ClampToScreen(screenLocation, Size);

        // No focus → no Deactivate event; poll for an outside click to close.
        var t = new System.Windows.Forms.Timer { Interval = 60 };
        t.Tick += (_, _) => CheckOutsideClick();
        t.Start();
        FormClosed += (_, _) => t.Stop();
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

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate) { m.Result = (IntPtr)MaNoActivate; return; }
        base.WndProc(ref m);
    }

    private int MeasureWidth()
    {
        int max = MinW;
        foreach (var (_, title) in _items)
        {
            var text = string.IsNullOrWhiteSpace(title) ? "(no title)" : title;
            int w = TextRenderer.MeasureText(text, Font).Width + PadX * 2;
            if (w > max) max = w;
        }
        return Math.Min(MaxW, max) + 2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, "(no windows)", Font, new Point(PadX, 5), Color.Gray);
        }
        else
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var rect = new Rectangle(1, 1 + i * ItemH, ClientSize.Width - 2, ItemH);
                bool hot = i == _hover;
                if (hot)
                    using (var b = new SolidBrush(Color.FromArgb(45, 90, 200)))
                        g.FillRectangle(b, rect);

                var title = _items[i].title;
                var text = string.IsNullOrWhiteSpace(title) ? "(no title)" : title;
                var textRect = new Rectangle(rect.X + PadX, rect.Y, rect.Width - PadX * 2, rect.Height);
                TextRenderer.DrawText(g, text, Font, textRect,
                    hot ? Color.White : Color.FromArgb(210, 218, 240),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        using var pen = new Pen(Color.FromArgb(70, 100, 200));
        g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = HitTest(e.Y);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int i = HitTest(e.Y);
        if (i >= 0) _engine.MoveWindowToCellWrapped(_items[i].hwnd, _col, _row);
        Close();
    }

    private int HitTest(int y)
    {
        int i = (y - 1) / ItemH;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    private void CheckOutsideClick()
    {
        bool lmb = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool rmb = (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
        if ((lmb || rmb) && !Bounds.Contains(Cursor.Position))
            Close();
    }

    private static Point ClampToScreen(Point p, Size size)
    {
        var wa = Screen.GetWorkingArea(p);
        int x = Math.Min(p.X, wa.Right - size.Width);
        int y = Math.Min(p.Y, wa.Bottom - size.Height);
        return new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
    }
}
