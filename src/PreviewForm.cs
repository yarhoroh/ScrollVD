namespace ScrollVD;

/// <summary>
/// Tiny borderless top-most popup that shows a thumbnail of a minimap cell's
/// contents while the user hovers over that cell. Never takes focus.
/// </summary>
internal sealed class PreviewForm : Form
{
    private Bitmap? _img;
    private string _label = "";
    private const int Border = 3;

    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    public PreviewForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(70, 100, 200); // shows as a thin frame around the image
        DoubleBuffered = true;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_img is null) return;
        var g = e.Graphics;
        g.DrawImage(_img, Border, Border, _img.Width, _img.Height);

        // Big translucent cell number in the centre
        if (_label.Length > 0)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float fs = _img.Height * 0.5f;
            using var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var rect = new RectangleF(Border, Border, _img.Width, _img.Height);
            using var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            using var fore = new SolidBrush(Color.FromArgb(130, 255, 255, 255));
            g.DrawString(_label, f, shadow, new RectangleF(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), sf);
            g.DrawString(_label, f, fore, rect, sf);
        }
    }

    /// <summary>Show the thumbnail (with an optional centre label) at the given screen location.</summary>
    public void ShowAt(Bitmap img, Point screenLocation, string label = "")
    {
        _img?.Dispose();
        _img = img;
        _label = label;
        Size = new Size(img.Width + Border * 2, img.Height + Border * 2);
        Location = screenLocation;
        if (!Visible) Show();
        Invalidate();
    }

    public void HidePreview()
    {
        if (Visible) Hide();
        _img?.Dispose();
        _img = null;
    }
}
