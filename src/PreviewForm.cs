namespace ScrollVD;

/// <summary>
/// Tiny borderless top-most popup that shows a thumbnail of a minimap cell's
/// contents while the user hovers over that cell. Never takes focus.
/// </summary>
internal sealed class PreviewForm : Form
{
    private Bitmap? _img;
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
        if (_img is not null)
            e.Graphics.DrawImage(_img, Border, Border, _img.Width, _img.Height);
    }

    /// <summary>Show the thumbnail at the given screen location (does not steal focus).</summary>
    public void ShowAt(Bitmap img, Point screenLocation)
    {
        _img?.Dispose();
        _img = img;
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
