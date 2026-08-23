using System.Drawing.Drawing2D;

namespace SBBackup.Ui;

/// <summary>Fila navegable dentro de una tarjeta (título + subtítulo opcional + chevron).</summary>
internal sealed class NavRowControl : Control
{
    private bool _hover;

    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }

    public event EventHandler? RowClick;

    public NavRowControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint,
            true);
        Height = 44;
        MinimumSize = new Size(0, 44);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
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
        _hover = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        RowClick?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            RowClick?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var parentBg = Parent?.BackColor ?? UiTheme.Surface;
        using (var clear = new SolidBrush(parentBg))
            g.FillRectangle(clear, ClientRectangle);

        var padX = UiTheme.Scale(this, 4);
        var padY = UiTheme.Scale(this, 2);
        var rect = new Rectangle(padX, padY, Width - padX * 2 - 1, Height - padY * 2 - 1);
        if (rect.Width > 8 && rect.Height > 8)
        {
            using var path = UiTheme.RoundedRectangle(rect, UiTheme.ScaledCornerRadius(this, 8));
            using var fill = new SolidBrush(_hover ? UiTheme.PrimarySoft : Color.FromArgb(250, 251, 252));
            g.FillPath(fill, path);
            if (_hover)
            {
                using var pen = new Pen(UiTheme.Primary, 1f);
                g.DrawPath(pen, path);
            }
        }

        var textLeft = UiTheme.Scale(this, 16);
        var textRight = UiTheme.Scale(this, 40);
        var titleTop = Subtitle is null ? 0 : UiTheme.Scale(this, 6);
        var titleH = Subtitle is null ? Height : UiTheme.Scale(this, 20);
        var titleRect = new Rectangle(textLeft, titleTop, Width - textRight, titleH);
        TextRenderer.DrawText(g, Title, UiTheme.UiFont(9.75f, FontStyle.Bold), titleRect,
            UiTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (!string.IsNullOrWhiteSpace(Subtitle))
        {
            var subRect = new Rectangle(textLeft, UiTheme.Scale(this, 24), Width - textRight, UiTheme.Scale(this, 16));
            TextRenderer.DrawText(g, Subtitle, UiTheme.UiFont(8.25f), subRect,
                UiTheme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        var chevronRect = new Rectangle(Width - UiTheme.Scale(this, 28), 0, UiTheme.Scale(this, 24), Height);
        TextRenderer.DrawText(g, "›", UiTheme.UiFont(14f, FontStyle.Bold), chevronRect,
            _hover ? UiTheme.PrimaryDark : UiTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
