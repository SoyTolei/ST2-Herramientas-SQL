using System.Drawing.Drawing2D;

namespace SBBackup.Ui;

/// <summary>Botón de acción principal con esquinas redondeadas (sin recorte por Region).</summary>
internal sealed class RoundedActionButton : Button
{
    private Color _normal = UiTheme.Primary;
    private Color _hover = UiTheme.PrimaryDark;
    private Color _border = Color.Transparent;
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
        UseCompatibleTextRendering = false;
    }

    public void SetColors(Color normal, Color hover, Color? border = null)
    {
        _normal = normal;
        _hover = hover;
        if (border.HasValue)
            _border = border.Value;
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

        var sharp = UiTheme.UseSharpRects(this);
        UiTheme.ConfigureCrispGraphics(g, this, curves: !sharp);
        using var brush = new SolidBrush(fill);
        if (sharp)
        {
            g.FillRectangle(brush, rect);
            if (_border.A > 0)
            {
                using var pen = new Pen(_border, Math.Max(2f, UiTheme.BorderWidth(this)));
                g.DrawRectangle(pen, rect);
            }
        }
        else
        {
            using var path = UiTheme.RoundedRectangle(rect, UiTheme.ScaledCornerRadius(this, 8));
            g.FillPath(brush, path);
            if (_border.A > 0)
            {
                using var pen = new Pen(_border, Math.Max(2f, UiTheme.BorderWidth(this)));
                g.DrawPath(pen, path);
            }
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : UiTheme.TextMuted, flags);
    }
}
