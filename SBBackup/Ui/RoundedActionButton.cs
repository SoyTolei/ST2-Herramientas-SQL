using System.Drawing.Drawing2D;

namespace SBBackup.Ui;

/// <summary>Botón de acción principal con esquinas redondeadas (sin recorte por Region).</summary>
internal sealed class RoundedActionButton : Button
{
    private Color _normal = UiTheme.Primary;
    private Color _hover = UiTheme.PrimaryDark;
    private bool _hovering;

    public RoundedActionButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.White;
        Font = UiTheme.UiFont(10.25f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Padding = new Padding(18, 8, 18, 8);
        Margin = new Padding(0, 4, 0, 4);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(0, 36);
        UseCompatibleTextRendering = true;
    }

    public void SetColors(Color normal, Color hover)
    {
        _normal = normal;
        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovering = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovering = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var parentBg = Parent?.BackColor ?? UiTheme.AppBack;
        using (var clear = new SolidBrush(parentBg))
            g.FillRectangle(clear, ClientRectangle);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width < 8 || rect.Height < 8)
            return;

        var fill = !Enabled
            ? Color.FromArgb(190, 190, 190)
            : _hovering
                ? _hover
                : _normal;

        using var path = UiTheme.RoundedRectangle(rect, UiTheme.ScaledCornerRadius(this, 10));
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.WordEllipsis;
        TextRenderer.DrawText(g, Text, Font, rect, Enabled ? ForeColor : UiTheme.TextMuted, flags);
    }
}
