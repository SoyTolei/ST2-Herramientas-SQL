using SBBackup;

namespace SBBackup.Ui;

internal static class CardSection
{
    public static TableLayoutPanel CreateFill(string title, Control content, Padding? margin = null)
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Margin = margin ?? new Padding(0, 0, 0, 4)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.SectionTitleHeight));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var lbl = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(4, 0, 0, 2),
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.SectionTitleFont(),
            BackColor = UiTheme.AppBack
        };
        outer.Controls.Add(lbl, 0, 0);

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(4),
            Margin = new Padding(0)
        };
        card.Paint += PaintCardBorder;
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        outer.Controls.Add(card, 0, 1);
        return outer;
    }

    /// <summary>Tarjeta compacta de altura fija (p. ej. carpeta de destino, progreso).</summary>
    public static TableLayoutPanel CreateFixed(string title, Control content, int contentHeight, Padding? margin = null)
    {
        var totalH = UiTheme.CardSectionFixedHeight(contentHeight);
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = totalH,
            MinimumSize = new Size(0, totalH),
            MaximumSize = new Size(10000, totalH),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Margin = margin ?? new Padding(0, 0, 0, 6)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.SectionTitleHeight));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, totalH - UiTheme.SectionTitleHeight));

        var lbl = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(4, 0, 0, 2),
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.SectionTitleFont(),
            BackColor = UiTheme.AppBack
        };
        outer.Controls.Add(lbl, 0, 0);

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(8, 6, 8, 6)
        };
        card.Paint += PaintCardBorder;
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        outer.Controls.Add(card, 0, 1);
        return outer;
    }

    public static TableLayoutPanel Create(string title, Control content, Padding? margin = null)
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Margin = margin ?? new Padding(0, 0, 0, 6)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lbl = new Label
        {
            Text = title,
            AutoSize = true,
            Margin = new Padding(4, 0, 0, 6),
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.SectionTitleFont(),
            BackColor = UiTheme.AppBack
        };
        outer.Controls.Add(lbl, 0, 0);

        var card = new Panel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = UiTheme.Surface,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(0)
        };
        card.Paint += PaintCardBorder;
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        outer.Controls.Add(card, 0, 1);

        return outer;
    }

    public static Panel CreateFillCard(string title)
    {
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.AppBack,
            Margin = new Padding(0, 0, 0, 4)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var lbl = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(4, 0, 0, 4),
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.SectionTitleFont(),
            BackColor = UiTheme.AppBack
        };
        layout.Controls.Add(lbl, 0, 0);

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(8, 6, 8, 8)
        };
        card.Paint += PaintCardBorder;
        layout.Controls.Add(card, 0, 1);
        outer.Controls.Add(layout);
        card.Tag = "contentHost";
        return outer;
    }

    public static Panel GetContentHost(Panel fillCardOuter)
    {
        foreach (Control c in fillCardOuter.Controls)
        {
            if (c is not TableLayoutPanel layout)
                continue;
            foreach (Control c2 in layout.Controls)
            {
                if (c2 is Panel { Tag: "contentHost" } host)
                    return host;
            }
        }

        return fillCardOuter;
    }

    public static Panel CreateInlineBarFill(Control content, Padding? margin = null)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            BackColor = UiTheme.Surface,
            Padding = new Padding(12, 8, 12, 8),
            Margin = margin ?? new Padding(0, 0, 0, 4)
        };
        card.Paint += PaintCardBorder;
        content.Dock = DockStyle.Fill;
        content.AutoSize = false;
        if (content is TableLayoutPanel grid)
            grid.AutoSize = false;
        card.Controls.Add(content);
        return card;
    }

    private static void PaintCardBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel panel)
            return;

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
        if (rect.Width <= 2 || rect.Height <= 2)
            return;

        using var path = UiTheme.RoundedRectangle(rect, 12);
        using var pen = new Pen(UiTheme.Border, 1f);
        e.Graphics.DrawPath(pen, path);
    }
}
