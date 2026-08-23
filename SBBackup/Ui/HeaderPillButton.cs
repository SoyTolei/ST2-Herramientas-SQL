using System.Drawing.Drawing2D;

namespace SBBackup.Ui;

/// <summary>Botón pill blanco sobre la barra naranja del encabezado.</summary>
internal sealed class HeaderPillButton : Control
{
    private bool _hover;
    private bool _pressed;

    public HeaderPillButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Size = new Size(148, 34);
        MinimumSize = new Size(120, 34);
        MaximumSize = new Size(220, 34);
        Font = UiTheme.UiFont(9.75f, FontStyle.Bold);
        ForeColor = UiTheme.PrimaryDark;
        BackColor = Color.Transparent;
        TabStop = true;
    }

    public event EventHandler? Clicked;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(2, 2, Width - 5, Height - 5);
        if (rect.Width <= 8 || rect.Height <= 8)
            return;

        var radius = Math.Min(rect.Height / 2, 14);
        var fill = !Enabled
            ? Color.FromArgb(200, 200, 200)
            : _pressed
                ? Color.FromArgb(235, 235, 235)
                : _hover
                    ? Color.FromArgb(255, 255, 255)
                    : Color.FromArgb(252, 252, 252);

        using var path = UiTheme.RoundedRectangle(rect, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);

        using var pen = new Pen(Color.FromArgb(200, 200, 200), 1f);
        g.DrawPath(pen, path);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Text, Font, rect,
            Enabled ? ForeColor : UiTheme.TextMuted, flags);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _pressed && ClientRectangle.Contains(e.Location))
            Clicked?.Invoke(this, EventArgs.Empty);
        _pressed = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Enter or Keys.Space && _pressed)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
            _pressed = false;
            Invalidate();
        }
    }
}
