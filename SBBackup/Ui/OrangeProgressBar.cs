using System.Drawing.Drawing2D;

namespace SBBackup.Ui;

/// <summary>
/// Barra de progreso dibujada a mano en la paleta naranja Bejerman,
/// para no usar el verde por defecto de Windows.
/// </summary>
internal sealed class OrangeProgressBar : Control
{
    private int _value;
    private int _maxBarHeight;
    private bool _marquee;
    private double _marqueeOffset;
    private System.Windows.Forms.Timer? _marqueeTimer;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;

    /// <summary>
    /// Modo indeterminado: para operaciones sin un % real (ej. conectar al servidor),
    /// muestra un segmento animado en loop en vez de un valor fijo.
    /// </summary>
    public bool Marquee
    {
        get => _marquee;
        set
        {
            if (_marquee == value)
                return;
            _marquee = value;
            if (_marquee)
            {
                _marqueeTimer ??= new System.Windows.Forms.Timer { Interval = 30 };
                _marqueeTimer.Tick -= OnMarqueeTick;
                _marqueeTimer.Tick += OnMarqueeTick;
                _marqueeTimer.Start();
            }
            else
            {
                _marqueeTimer?.Stop();
                _marqueeOffset = 0;
            }
            Invalidate();
        }
    }

    private void OnMarqueeTick(object? sender, EventArgs e)
    {
        _marqueeOffset += 0.02;
        if (_marqueeOffset > 1.4)
            _marqueeOffset = -0.4;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _marqueeTimer?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Limita la altura al dibujar la barra (0 = sin límite).</summary>
    public int MaxBarHeight
    {
        get => _maxBarHeight;
        set
        {
            _maxBarHeight = Math.Max(0, value);
            if (_maxBarHeight > 0 && Height > _maxBarHeight)
                Height = _maxBarHeight;
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value)
                return;
            _value = clamped;
            Invalidate();
        }
    }

    public OrangeProgressBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (_maxBarHeight > 0 && (specified & BoundsSpecified.Height) != 0)
            height = Math.Min(height, _maxBarHeight);
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 2 || rect.Height <= 2)
            return;

        var radius = Math.Min(10, rect.Height / 2);

        using var track = UiTheme.RoundedRectangle(rect, radius);
        using (var bg = new SolidBrush(UiTheme.PrimarySoft))
            g.FillPath(bg, track);

        if (_marquee)
        {
            var chunkWidth = Math.Max(24, rect.Width / 3);
            var chunkX = rect.X + (int)Math.Round(_marqueeOffset * rect.Width);
            var chunkRect = new Rectangle(chunkX, rect.Y, chunkWidth, rect.Height);
            using var grad = new LinearGradientBrush(
                new Rectangle(chunkRect.X - 1, chunkRect.Y, chunkRect.Width + 2, chunkRect.Height),
                UiTheme.PrimarySoft, UiTheme.Primary, LinearGradientMode.Horizontal);
            var state = g.Save();
            g.SetClip(track);
            g.FillRectangle(grad, chunkRect);
            g.Restore(state);
        }
        else
        {
            var range = Math.Max(1, Maximum - Minimum);
            var fraction = (double)(_value - Minimum) / range;
            var fillWidth = (int)Math.Round(rect.Width * fraction);

            if (fillWidth > 2)
            {
                var fillRect = new Rectangle(rect.X, rect.Y, fillWidth, rect.Height);
                using var grad = new LinearGradientBrush(
                    fillRect, UiTheme.Primary, UiTheme.PrimaryDark, LinearGradientMode.Horizontal);
                var state = g.Save();
                g.SetClip(track);
                g.FillRectangle(grad, fillRect);
                g.Restore(state);
            }
        }

        using var pen = new Pen(UiTheme.Border, 1f);
        g.DrawPath(pen, track);
    }
}
