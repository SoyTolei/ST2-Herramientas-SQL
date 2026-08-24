using System.Diagnostics;
using System.Drawing.Drawing2D;
using SBBackup.Ui;

namespace SBBackup;

internal static class UiTheme
{
    // Paleta Thomson Reuters / Bejerman (naranja corporativo #F35C19, #FF8000)
    internal static readonly Color AppBack = Color.FromArgb(247, 247, 247);
    internal static readonly Color Surface = Color.White;
    internal static readonly Color HeaderBg = Color.FromArgb(230, 74, 25);
    internal static readonly Color HeaderBgDeep = Color.FromArgb(194, 58, 18);
    internal static readonly Color HeaderAccentStripe = Color.FromArgb(255, 152, 80);

    internal static readonly Color Primary = Color.FromArgb(243, 92, 25);
    internal static readonly Color PrimaryDark = Color.FromArgb(211, 68, 14);
    internal static readonly Color PrimaryLight = Color.FromArgb(255, 224, 204);
    internal static readonly Color PrimarySoft = Color.FromArgb(255, 243, 235);

    // Azul distinto del naranja corporativo (Conectar vs acciones de backup)
    internal static readonly Color Connect = Color.FromArgb(30, 100, 175);
    internal static readonly Color ConnectDark = Color.FromArgb(22, 78, 138);

    internal static readonly Color TextPrimary = Color.FromArgb(68, 68, 68);
    internal static readonly Color TextMuted = Color.FromArgb(102, 102, 102);
    internal static readonly Color Border = Color.FromArgb(204, 204, 204);
    internal static readonly Color GridHeaderBg = Color.FromArgb(255, 248, 242);
    internal static readonly Color GridRowAlt = Color.FromArgb(250, 250, 250);
    internal static readonly Color GridSelection = Color.FromArgb(255, 228, 210);

    internal static readonly Color IncludeYesBg = Color.FromArgb(220, 252, 231);
    internal static readonly Color IncludeYesFg = Color.FromArgb(4, 120, 87);
    internal static readonly Color IncludeNoBg = Color.FromArgb(254, 242, 242);
    internal static readonly Color IncludeNoFg = Color.FromArgb(220, 38, 38);

    /// <summary>Radio estándar para esquinas redondeadas de botones (lógico @ 96 DPI).</summary>
    internal const int ButtonCornerRadius = 8;

    /// <summary>Factor de escala del monitor actual respecto a 96 DPI (1.0 = 100%).</summary>
    internal static float DpiScaleFactor(Control control)
    {
        var dpi = 96;
        try
        {
            if (control.IsHandleCreated)
                dpi = control.DeviceDpi;
        }
        catch
        {
            dpi = 96;
        }

        return Math.Max(1f, dpi / 96f);
    }

    /// <summary>Convierte píxeles lógicos (@96 DPI) a píxeles del monitor actual.</summary>
    internal static int Scale(Control control, int logicalPixels) =>
        (int)Math.Round(logicalPixels * DpiScaleFactor(control));

    internal static Size ScaleSize(Control control, int logicalWidth, int logicalHeight) =>
        new(Scale(control, logicalWidth), Scale(control, logicalHeight));

    /// <summary>
    /// Formularios creados a mano: si se pone AutoScaleMode.Dpi sin AutoScaleDimensions = 96×96,
    /// WinForms usa 6×13 (modo Font) como base y el layout queda borroso / deformado.
    /// </summary>
    internal static void ApplyDpiAwareScaling(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    internal static int ScaledCornerRadius(Control control, int logicalRadius = ButtonCornerRadius) =>
        Math.Max(2, Scale(control, logicalRadius));

    /// <summary>
    /// 125/150/175% (DPI 120/144/168): las curvas GDI+ de 1 px se ven sucias.
    /// En esos monitores dibujamos rectos, alineados al píxel.
    /// </summary>
    internal static bool UseSharpRects(Control control)
    {
        try
        {
            if (control.IsHandleCreated)
                return control.DeviceDpi is 120 or 144 or 168;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    internal static void ConfigureCrispGraphics(Graphics g, Control control, bool curves)
    {
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (!curves || UseSharpRects(control))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
        }
        else
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }
    }

    internal static float BorderWidth(Control control) =>
        Math.Max(1f, DpiScaleFactor(control) >= 1.5f ? 2f : 1f);

    internal static Font UiFont(float size = 10f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    /// <summary>Fuente del sistema para emojis en WinForms (evita el cuadrado □).</summary>
    internal static Font EmojiFont(float size = 10f) =>
        CreateEmojiFont(size);

    private static Font CreateEmojiFont(float size)
    {
        foreach (var family in new[] { "Segoe UI Emoji", "Segoe UI Symbol" })
        {
            try
            {
                return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                // probar siguiente familia
            }
        }

        return UiFont(size);
    }

    internal static Label CreateEmojiLabel(string emoji, float size, Color foreColor, Padding? margin = null) =>
        new()
        {
            Text = emoji,
            AutoSize = true,
            Font = EmojiFont(size),
            ForeColor = foreColor,
            BackColor = Color.Transparent,
            Margin = margin ?? new Padding(4, 0, 0, 0),
            UseCompatibleTextRendering = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

    internal static Font SectionTitleFont() =>
        UiFont(9f, FontStyle.Bold);

    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(d, d));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal const int SectionTitleHeight = 22;

    internal static int CardSectionFixedHeight(int innerContentHeight) =>
        SectionTitleHeight + innerContentHeight + 16;

    internal static void StylePrimaryButton(Button b) => StyleActionButton(b, Primary, PrimaryDark);

    internal static void StyleBackupButton(Button b)
    {
        if (b is RoundedActionButton rounded)
        {
            rounded.SetColors(Primary, PrimaryDark);
            rounded.Font = UiFont(10.25f, FontStyle.Bold);
            rounded.Padding = new Padding(18, 8, 18, 8);
            rounded.Margin = new Padding(0, 4, 0, 4);
            return;
        }

        StyleActionButton(b, Primary, PrimaryDark, 10.25f, new Padding(18, 8, 18, 8));
        b.Margin = new Padding(0, 4, 0, 4);
    }

    /// <summary>Botón de programación (azul, para contrastar con el naranja de backup).</summary>
    internal static void StyleScheduleButton(Button b)
    {
        if (b is RoundedActionButton rounded)
        {
            rounded.SetColors(Connect, ConnectDark);
            rounded.Font = UiFont(10.25f, FontStyle.Bold);
            rounded.Padding = new Padding(18, 8, 18, 8);
            rounded.Margin = new Padding(16, 4, 0, 4);
            return;
        }

        StyleActionButton(b, Connect, ConnectDark, 10.25f, new Padding(18, 8, 18, 8));
        b.Margin = new Padding(16, 4, 0, 4);
    }

    /// <summary>Acción de prueba / secundaria rellena (azul suave).</summary>
    internal static void StyleInfoButton(Button b)
    {
        if (b is RoundedActionButton rounded)
        {
            rounded.SetColors(Connect, ConnectDark);
            rounded.Font = UiFont(9.5f, FontStyle.Bold);
            rounded.Padding = new Padding(14, 8, 14, 8);
            rounded.Margin = new Padding(6, 4, 6, 4);
            return;
        }

        StyleActionButton(b, Connect, ConnectDark, 9.5f, new Padding(14, 8, 14, 8));
        b.Margin = new Padding(6, 4, 6, 4);
    }

    /// <summary>Desactivar / peligro (rojo).</summary>
    internal static void StyleDangerButton(Button b)
    {
        if (b is RoundedActionButton rounded)
        {
            rounded.SetColors(Danger, DangerDark);
            rounded.Font = UiFont(9.5f, FontStyle.Bold);
            rounded.Padding = new Padding(14, 8, 14, 8);
            rounded.Margin = new Padding(6, 4, 6, 4);
            return;
        }

        StyleActionButton(b, Danger, DangerDark, 9.5f, new Padding(14, 8, 14, 8));
        b.Margin = new Padding(6, 4, 6, 4);
    }

    internal static void StyleRestoreButton(Button b) => StyleBackupButton(b);

    internal static void StyleMenuButton(Button b, bool primary)
    {
        b.AutoSize = false;
        b.Height = 44;
        b.MinimumSize = new Size(0, 44);
        b.Dock = DockStyle.Fill;
        b.Margin = primary ? new Padding(0, 0, 0, 6) : new Padding(0);
        b.Font = UiFont(10.25f, FontStyle.Bold);
        b.UseCompatibleTextRendering = false;
        b.Cursor = Cursors.Hand;
        b.Padding = new Padding(14, 10, 14, 10);

        if (b is RoundedActionButton rounded)
        {
            if (primary)
            {
                rounded.SetColors(Primary, PrimaryDark, Color.Transparent);
                rounded.ForeColor = Color.White;
            }
            else
            {
                rounded.SetColors(Surface, PrimarySoft, Primary);
                rounded.ForeColor = PrimaryDark;
            }
            return;
        }

        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Primary;

        if (primary)
        {
            b.BackColor = Primary;
            b.ForeColor = Color.White;
            b.MouseEnter += (_, _) => b.BackColor = PrimaryDark;
            b.MouseLeave += (_, _) => b.BackColor = Primary;
        }
        else
        {
            b.BackColor = Surface;
            b.ForeColor = PrimaryDark;
            b.MouseEnter += (_, _) => b.BackColor = PrimarySoft;
            b.MouseLeave += (_, _) => b.BackColor = Surface;
        }
    }

    /// <summary>Botón secundario compacto (herramientas auxiliares, separado de las acciones principales).</summary>
    internal static void StyleUtilityMenuButton(Button b)
    {
        b.AutoSize = false;
        b.Height = 32;
        b.MinimumSize = new Size(0, 32);
        b.MaximumSize = new Size(10000, 32);
        b.Dock = DockStyle.Fill;
        b.Font = UiFont(8.75f, FontStyle.Bold);
        b.UseCompatibleTextRendering = false;
        b.Cursor = Cursors.Hand;
        b.Padding = new Padding(12, 6, 12, 6);
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.BackColor = Color.FromArgb(248, 249, 251);
        b.ForeColor = Color.FromArgb(90, 98, 110);
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(210, 216, 224);
        b.MouseEnter += (_, _) =>
        {
            b.BackColor = PrimarySoft;
            b.ForeColor = PrimaryDark;
            b.FlatAppearance.BorderColor = Primary;
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = Color.FromArgb(248, 249, 251);
            b.ForeColor = Color.FromArgb(90, 98, 110);
            b.FlatAppearance.BorderColor = Color.FromArgb(210, 216, 224);
        };
    }

    internal static void StyleConnectButton(Button b) => StyleConnectButtonForBar(b);

    internal static void StyleConnectButtonForBar(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Connect;
        b.ForeColor = Color.White;
        b.Font = UiFont(9.75f, FontStyle.Regular);
        b.Cursor = Cursors.Hand;
        b.Padding = new Padding(6, 4, 6, 4);
        b.Margin = new Padding(0, 2, 0, 2);
        b.AutoSize = false;
        b.UseVisualStyleBackColor = false;
        b.UseCompatibleTextRendering = false;
        b.MouseEnter += (_, _) => b.BackColor = ConnectDark;
        b.MouseLeave += (_, _) => b.BackColor = Connect;
    }

    internal static void StyleFolderPickerButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = Surface;
        b.ForeColor = Connect;
        b.Font = UiFont(9.5f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.AutoSize = false;
        b.Dock = DockStyle.None;
        b.Size = new Size(96, 30);
        b.MinimumSize = b.Size;
        b.MaximumSize = b.Size;
        b.Margin = new Padding(12, 0, 0, 0);
        b.Padding = new Padding(6, 4, 6, 4);
        b.UseVisualStyleBackColor = false;
        b.UseCompatibleTextRendering = false;
        b.MouseEnter += (_, _) =>
        {
            b.BackColor = Color.FromArgb(232, 248, 242);
            b.FlatAppearance.BorderColor = Connect;
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = Surface;
            b.FlatAppearance.BorderColor = Border;
        };
    }

    internal static void StyleSecondaryButton(Button b) => StyleOutlineButton(b, Primary);

    internal static readonly Color Danger = Color.FromArgb(200, 35, 35);
    internal static readonly Color DangerDark = Color.FromArgb(170, 25, 25);

    internal static Panel CreateSubFormHeader(string title, EventHandler? onBackClick)
    {
        var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 18, 0) };
        header.Paint += (_, e) => PaintAppHeaderBackground(e.Graphics, header.ClientRectangle);

        var lay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        lay.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        if (onBackClick is not null)
        {
            var back = new Ui.HeaderPillButton
            {
                Text = "← Volver",
                Size = new Size(108, 26),
                Margin = new Padding(0, 16, 10, 16),
                Anchor = AnchorStyles.Left
            };
            back.Clicked += onBackClick;
            lay.Controls.Add(back, 0, 0);
        }

        // Misma tipografía en Backup / Query / Traza / Restaurar.
        var titleLbl = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.White,
            Font = UiFont(14f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 4, 0),
            Margin = new Padding(0, 14, 0, 14),
            AutoEllipsis = true,
            UseCompatibleTextRendering = false
        };
        lay.Controls.Add(titleLbl, 1, 0);

        header.Controls.Add(lay);
        return header;
    }

    internal static void PaintAppHeaderBackground(Graphics g, Rectangle rect)
    {
        using var brush = new LinearGradientBrush(rect, HeaderBg, HeaderBgDeep, LinearGradientMode.Horizontal);
        g.FillRectangle(brush, rect);
        using var stripe = new SolidBrush(HeaderAccentStripe);
        g.FillRectangle(stripe, 0, 0, 5, rect.Height);
    }

    internal static Panel CreateAlertBanner(string title, string body, Color accent, Color bg, Color fg)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = bg,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            if (r.Width <= 4)
                return;
            using var path = RoundedRectangle(r, 10);
            using var pen = new Pen(Color.FromArgb(40, accent), 1f);
            e.Graphics.DrawPath(pen, path);
            using var stripe = new SolidBrush(accent);
            e.Graphics.FillRectangle(stripe, 0, 0, 4, panel.Height);
        };

        var lay = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        lay.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = fg,
            Font = UiFont(10f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Margin = new Padding(6, 0, 0, 2)
        }, 0, 0);
        lay.Controls.Add(new Label
        {
            Text = body,
            AutoSize = true,
            ForeColor = fg,
            Font = UiFont(9.5f),
            BackColor = Color.Transparent,
            Margin = new Padding(6, 0, 0, 0)
        }, 0, 1);
        panel.Controls.Add(lay);
        return panel;
    }

    /// <summary>Aviso con título y cuerpo para filas de altura fija.</summary>
    internal static Panel CreateCompactAlertBanner(string title, string body, Color accent, Color bg, Color fg)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 0, 0, 2)
        };
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            if (r.Width <= 4)
                return;
            using var path = RoundedRectangle(r, 10);
            using var pen = new Pen(Color.FromArgb(40, accent), 1f);
            e.Graphics.DrawPath(pen, path);
            using var stripe = new SolidBrush(accent);
            e.Graphics.FillRectangle(stripe, 0, 0, 4, panel.Height);
        };

        var lay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 17f));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
        lay.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = fg,
            Font = UiFont(9.25f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = false
        }, 0, 0);
        lay.Controls.Add(new Label
        {
            Text = body,
            Dock = DockStyle.Fill,
            ForeColor = fg,
            Font = UiFont(8.75f),
            BackColor = Color.Transparent,
            Padding = new Padding(8, 0, 4, 0),
            TextAlign = ContentAlignment.TopLeft,
            UseCompatibleTextRendering = false,
            AutoEllipsis = false
        }, 0, 1);
        panel.Controls.Add(lay);
        return panel;
    }

    /// <summary>Aviso informativo sin título (solo texto).</summary>
    internal static Panel CreateInfoBanner(string body, Color accent, Color bg, Color fg, bool compact = false)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            BackColor = bg,
            Padding = compact ? new Padding(10, 5, 10, 5) : new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, compact ? 2 : 4)
        };
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            if (r.Width <= 4)
                return;
            using var path = RoundedRectangle(r, 10);
            using var pen = new Pen(Color.FromArgb(40, accent), 1f);
            e.Graphics.DrawPath(pen, path);
            using var stripe = new SolidBrush(accent);
            e.Graphics.FillRectangle(stripe, 0, 0, 4, panel.Height);
        };

        panel.Controls.Add(new Label
        {
            Text = body,
            Dock = DockStyle.Fill,
            ForeColor = fg,
            Font = UiFont(compact ? 9f : 9.25f),
            BackColor = Color.Transparent,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseCompatibleTextRendering = false
        });
        return panel;
    }
    internal static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes} min {t.Seconds} seg";
        return $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} seg";
    }

    internal static string FormatProgressStatus(string status, int percent, Stopwatch sw)
    {
        var n = Math.Clamp(percent, 0, 100);
        var timeInfo = " · ⏱ " + FormatDuration(sw.Elapsed) + " transcurrido";
        if (n is > 1 and < 100 && sw.Elapsed.TotalSeconds >= 3)
        {
            var remaining = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (100 - n) / n);
            if (remaining.TotalSeconds >= 5)
                timeInfo += " · ~" + FormatDuration(remaining) + " restante";
        }

        return status + timeInfo;
    }

    internal static void StyleActionButton(
        Button b,
        Color normal,
        Color hover,
        float fontSize = 10f,
        Padding? padding = null)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = ControlPaint.Dark(normal);
        b.BackColor = normal;
        b.ForeColor = Color.White;
        b.Font = UiFont(fontSize, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.Padding = padding ?? new Padding(18, 9, 18, 9);
        b.Margin = new Padding(0, 4, 0, 4);
        b.UseVisualStyleBackColor = false;
        b.UseCompatibleTextRendering = false;
        b.MouseEnter += (_, _) =>
        {
            b.BackColor = hover;
            b.FlatAppearance.BorderColor = ControlPaint.Dark(hover);
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = normal;
            b.FlatAppearance.BorderColor = ControlPaint.Dark(normal);
        };
    }

    internal static void ApplyRoundedCorners(Control control, int radius)
    {
        void UpdateRegion(object? _, EventArgs __)
        {
            if (control.Width < 4 || control.Height < 4)
                return;
            var r = Math.Max(2, Scale(control, radius));
            using var path = RoundedRectangle(new Rectangle(0, 0, control.Width, control.Height), r);
            control.Region = new Region((GraphicsPath)path.Clone());
        }

        control.Resize += UpdateRegion;
        control.HandleCreated += UpdateRegion;
        if (control.IsHandleCreated)
            UpdateRegion(null, EventArgs.Empty);
    }

    /// <summary>
    /// Ajusta el tamaño de un subformulario al área de trabajo, respetando el DPI.
    /// <paramref name="preferredWidth"/> / <paramref name="preferredHeight"/> son tamaños
    /// lógicos pensados a 96 DPI; se multiplican por el factor del monitor antes de aplicarlos.
    /// (Si no, AutoScale agranda los Absolute internos y después se fuerza un ClientSize chico → botones deformados.)
    /// </summary>
    internal static void SizeSubFormToScreen(Form form, int preferredWidth, int preferredHeight)
    {
        if (!form.IsHandleCreated)
            return;
        var area = Screen.FromControl(form).WorkingArea;
        var maxH = Math.Max(Scale(form, 320), area.Height - Scale(form, 32));
        var maxW = Math.Max(Scale(form, 480), area.Width - Scale(form, 24));

        var scaledPreferredW = Scale(form, preferredWidth);
        var scaledPreferredH = Scale(form, preferredHeight);
        var h = Math.Clamp(scaledPreferredH, Math.Min(Scale(form, 460), maxH), maxH);
        var w = Math.Clamp(scaledPreferredW, Math.Min(Scale(form, 640), maxW), maxW);

        // El escalado de Windows también agranda el MinimumSize del constructor.
        if (form.MinimumSize.Width > w || form.MinimumSize.Height > h)
        {
            form.MinimumSize = new Size(
                Math.Min(form.MinimumSize.Width, w),
                Math.Min(form.MinimumSize.Height, h));
        }

        form.ClientSize = new Size(w, h);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(
            area.Left + Math.Max(0, (area.Width - form.Width) / 2),
            area.Top + Math.Max(0, (area.Height - form.Height) / 2));
    }

    /// <summary>Aplica <see cref="SizeSubFormToScreen"/> al mostrar y al cambiar de monitor/DPI.</summary>
    internal static void BindSubFormScreenSizing(Form form, int preferredWidth, int preferredHeight)
    {
        void Apply(object? _ = null, EventArgs? __ = null) =>
            SizeSubFormToScreen(form, preferredWidth, preferredHeight);

        form.Shown -= Apply;
        form.Shown += Apply;
        form.DpiChanged -= OnDpiChanged;
        form.DpiChanged += OnDpiChanged;

        void OnDpiChanged(object? sender, DpiChangedEventArgs e) => Apply();
    }

    /// <summary>
    /// Para ventanas de tamaño fijo (FormBorderStyle.FixedSingle, sin Resize). El
    /// tamaño se define pensando en 96 DPI; con escalados altos (125/150/175/200%…)
    /// AutoScaleMode.Dpi lo multiplica y puede terminar más grande que la pantalla.
    /// Esto la vuelve a acomodar dentro del área de trabajo disponible.
    /// </summary>
    internal static void EnsureFixedFormFitsScreen(Form form)
    {
        if (!form.IsHandleCreated)
            return;
        var area = Screen.FromControl(form).WorkingArea;
        var maxW = Math.Max(Scale(form, 360), area.Width - Scale(form, 24));
        var maxH = Math.Max(Scale(form, 320), area.Height - Scale(form, 32));

        if (form.MaximumSize.Width > 0 && form.MaximumSize.Width > maxW
            || form.MaximumSize.Height > 0 && form.MaximumSize.Height > maxH)
        {
            form.MaximumSize = new Size(
                form.MaximumSize.Width > 0 ? Math.Min(form.MaximumSize.Width, maxW) : 0,
                form.MaximumSize.Height > 0 ? Math.Min(form.MaximumSize.Height, maxH) : 0);
        }
        if (form.MinimumSize.Width > maxW || form.MinimumSize.Height > maxH)
        {
            form.MinimumSize = new Size(
                Math.Min(form.MinimumSize.Width, maxW),
                Math.Min(form.MinimumSize.Height, maxH));
        }
        if (form.ClientSize.Width > maxW || form.ClientSize.Height > maxH)
        {
            form.ClientSize = new Size(
                Math.Min(form.ClientSize.Width, maxW),
                Math.Min(form.ClientSize.Height, maxH));
        }

        form.Location = new Point(
            area.Left + Math.Max(0, (area.Width - form.Width) / 2),
            area.Top + Math.Max(0, (area.Height - form.Height) / 2));
    }

    /// <summary>Reaplica <see cref="EnsureFixedFormFitsScreen"/> al mostrar y al cambiar DPI.</summary>
    internal static void BindFixedFormScreenFit(Form form)
    {
        void Apply(object? _ = null, EventArgs? __ = null) => EnsureFixedFormFitsScreen(form);
        form.Shown -= Apply;
        form.Shown += Apply;
        form.DpiChanged -= OnDpiChanged;
        form.DpiChanged += OnDpiChanged;
        void OnDpiChanged(object? sender, DpiChangedEventArgs e) => Apply();
    }

    internal static Label CreateConnectionStatusLabel(bool centered = false)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = centered ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft,
            Font = UiFont(9.5f, FontStyle.Bold),
            ForeColor = TextMuted,
            BackColor = Color.Transparent,
            Text = "● Sin conexión"
        };
    }

    internal static void SetConnectionStatusLabel(Label label, bool connected, string? server)
    {
        if (connected && !string.IsNullOrWhiteSpace(server))
        {
            label.Text = "● Conectado a " + server;
            label.ForeColor = IncludeYesFg;
        }
        else
        {
            label.Text = "● Sin conexión";
            label.ForeColor = TextMuted;
        }
    }

    private static void StyleOutlineButton(Button b, Color accent)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = Surface;
        b.ForeColor = TextPrimary;
        b.Font = UiFont(9.75f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.Padding = new Padding(16, 8, 16, 8);
        b.Margin = new Padding(0, 4, 0, 4);
        b.UseVisualStyleBackColor = false;
        b.MouseEnter += (_, _) =>
        {
            b.BackColor = PrimarySoft;
            b.FlatAppearance.BorderColor = accent;
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = Surface;
            b.FlatAppearance.BorderColor = Border;
        };
    }

    internal static void StyleTextBox(TextBox t, bool compact = false)
    {
        t.BorderStyle = BorderStyle.FixedSingle;
        t.BackColor = Surface;
        t.ForeColor = TextPrimary;
        t.Font = UiFont(compact ? 9.75f : 10.25f);
        if (compact)
        {
            t.Height = 28;
            t.Margin = new Padding(0, 2, 0, 2);
        }
        else
            t.MinimumSize = new Size(0, 36);
    }

    internal static void StylePickFileButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = Surface;
        b.ForeColor = TextPrimary;
        b.Font = UiFont(9.75f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.AutoSize = false;
        b.Size = new Size(172, 30);
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.Padding = new Padding(8, 4, 8, 4);
        b.Margin = new Padding(0, 4, 0, 2);
        b.UseVisualStyleBackColor = false;
        b.UseCompatibleTextRendering = false;
        b.MouseEnter += (_, _) =>
        {
            b.BackColor = PrimarySoft;
            b.FlatAppearance.BorderColor = Primary;
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = Surface;
            b.FlatAppearance.BorderColor = Border;
        };
    }

    internal static void StyleComboBox(ComboBox c, bool compact = false)
    {
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Surface;
        c.ForeColor = TextPrimary;
        c.Font = UiFont(compact ? 10f : 10.25f);
        if (compact)
            c.Height = 30;
    }

    internal const int GridRowHeight = 26;
    internal const int GridHeaderHeight = 30;
    internal const int GridPreferredVisibleRows = 7;
    internal const int GridMinVisibleRows = 5;
    internal const int SubFormHeaderHeight = 58;
    internal const int AppHeaderHeight = 54;

    internal static int GridViewportHeight(int visibleRows = GridPreferredVisibleRows) =>
        GridHeaderHeight + (GridRowHeight * visibleRows) + 16;

    /// <summary>Alto total de la tarjeta de la grilla (título + borde + viewport).</summary>
    internal static int GridSectionHeight(int visibleRows = GridPreferredVisibleRows) =>
        GridViewportHeight(visibleRows) + 54;

    internal static void StyleEmpresaCombo(ComboBox c)
    {
        c.FlatStyle = FlatStyle.Standard;
        c.IntegralHeight = true;
        c.BackColor = Surface;
        c.ForeColor = TextPrimary;
        c.Font = UiFont(10f, FontStyle.Bold);
        c.Height = 28;
        c.DropDownWidth = 420;
    }

    internal static readonly Color EmpresaPanelBg = Color.FromArgb(238, 238, 238);

    internal static void StyleCompactFilledButton(Button b, Color normal, Color hover)
    {
        StyleActionButton(b, normal, hover, 9.5f, new Padding(0, 6, 0, 6));
        b.AutoSize = false;
        b.Size = new Size(88, 30);
        b.MinimumSize = new Size(88, 30);
        b.TextAlign = ContentAlignment.MiddleCenter;
    }

    internal static void StyleCheckBox(CheckBox c)
    {
        c.ForeColor = TextPrimary;
        c.Font = UiFont(10f);
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Color.Transparent;
    }

    internal static void StyleLabel(Label l, bool muted = false)
    {
        l.ForeColor = muted ? TextMuted : TextPrimary;
        l.Font = UiFont(10f);
        l.BackColor = Color.Transparent;
    }

    internal static void StyleDataGrid(DataGridView g)
    {
        g.BorderStyle = BorderStyle.None;
        g.BackgroundColor = Surface;
        g.GridColor = Border;
        g.EnableHeadersVisualStyles = false;
        g.RowTemplate.Height = GridRowHeight;
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = TextPrimary,
            SelectionBackColor = GridSelection,
            SelectionForeColor = TextPrimary,
            Font = UiFont(9.5f),
            Padding = new Padding(8, 4, 8, 4),
            WrapMode = DataGridViewTriState.False,
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle(g.DefaultCellStyle)
        {
            BackColor = GridRowAlt
        };
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = GridHeaderBg,
            ForeColor = TextPrimary,
            Font = SectionTitleFont(),
            Padding = new Padding(8, 4, 8, 4),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        g.ColumnHeadersHeight = GridHeaderHeight;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.RowHeadersVisible = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ScrollBars = ScrollBars.Vertical;
    }

    internal static Panel WrapGridFill(DataGridView grid)
    {
        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(2)
        };
        shell.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, shell.Width - 1, shell.Height - 1);
            if (rect.Width <= 4)
                return;
            using var path = RoundedRectangle(rect, 8);
            using var pen = new Pen(Border, 1f);
            e.Graphics.DrawPath(pen, path);
        };
        grid.Dock = DockStyle.Fill;
        shell.Controls.Add(grid);
        return shell;
    }

    internal static Panel WrapGrid(DataGridView grid, int? fixedVisibleRows = null)
    {
        var shell = new Panel
        {
            BackColor = Surface,
            Padding = new Padding(2)
        };
        if (fixedVisibleRows is int rows && rows > 0)
        {
            var h = GridViewportHeight(rows);
            shell.Dock = DockStyle.Top;
            shell.Height = h;
            shell.MinimumSize = new Size(200, h);
            shell.MaximumSize = new Size(10000, h);
        }
        else
        {
            shell.Dock = DockStyle.Fill;
        }
        shell.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, shell.Width - 1, shell.Height - 1);
            if (rect.Width <= 4)
                return;
            using var path = RoundedRectangle(rect, 8);
            using var pen = new Pen(Border, 1f);
            e.Graphics.DrawPath(pen, path);
        };
        grid.Dock = DockStyle.Fill;
        shell.Controls.Add(grid);
        return shell;
    }

    internal static Panel CreateProgressShell(out Ui.OrangeProgressBar bar, out Label percentLabel, bool compact = false)
    {
        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = compact ? new Padding(8, 4, 8, 4) : new Padding(10, 8, 10, 8),
            Margin = new Padding(0, 2, 0, 0),
            MinimumSize = new Size(0, compact ? 34 : 52)
        };
        shell.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, shell.Width - 1, shell.Height - 1);
            using var path = RoundedRectangle(rect, 10);
            using var pen = new Pen(Border, 1f);
            e.Graphics.DrawPath(pen, path);
        };

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, compact ? 56f : 68f));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, compact ? 24f : 32f));

        bar = new Ui.OrangeProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 100,
            Height = compact ? 18 : 24,
            Margin = new Padding(0, compact ? 1 : 2, 8, compact ? 1 : 2)
        };

        percentLabel = new Label
        {
            Text = "0 %",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted,
            Font = UiFont(compact ? 9f : 9.75f, FontStyle.Bold),
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = false
        };

        row.Controls.Add(bar, 0, 0);
        row.Controls.Add(percentLabel, 1, 0);
        shell.Controls.Add(row);
        return shell;
    }
}
