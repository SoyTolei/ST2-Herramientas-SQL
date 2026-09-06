using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

/// <summary>Pantalla de acceso antes del Home. Herramienta de uso interno ST2.</summary>
internal sealed class LoginForm : Form
{
    private const int MaxAttempts = 5;

    private TextBox _txtPassword = null!;
    private Label _lblStatus = null!;
    private Label _lblCapsLock = null!;
    private Button _btnEnter = null!;
    private int _attempts;

    public LoginForm()
    {
        Text = "ST2 · Acceso";
        UiTheme.ApplyDpiAwareScaling(this);
        Font = UiTheme.UiFont();
        BackColor = UiTheme.AppBack;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(400, 282);
        MinimumSize = new Size(400, 282);
        MaximumSize = new Size(400, 282);
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
        AppIcon.Apply(this);
        BuildUi();
    }

    private void BuildUi()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(22, 20, 22, 24)
        };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        shell.Controls.Add(BuildBrandRow(), 0, 0);
        shell.Controls.Add(BuildAccessCard(), 0, 1);
        Controls.Add(shell);

        AcceptButton = _btnEnter;
        Shown += (_, _) =>
        {
            _txtPassword.Focus();
            UpdateCapsLockHint();
        };
    }

    private Control BuildBrandRow()
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 16)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var icon = new PictureBox
        {
            Size = new Size(40, 40),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 0)
        };
        var iconImage = LoadBrandIcon(40);
        if (iconImage is not null)
            icon.Image = iconImage;

        var titles = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(4, 0, 0, 0)
        };
        titles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titles.Controls.Add(new Label
        {
            Text = "ST2",
            AutoSize = true,
            Font = UiTheme.UiFont(20f, FontStyle.Bold),
            ForeColor = UiTheme.Primary,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);
        titles.Controls.Add(new Label
        {
            Text = "Herramientas SQL · uso interno",
            AutoSize = true,
            Font = UiTheme.UiFont(9.5f),
            ForeColor = UiTheme.TextMuted,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        }, 0, 1);

        row.Controls.Add(icon, 0, 0);
        row.Controls.Add(titles, 1, 0);
        return row;
    }

    private Control BuildAccessCard()
    {
        var cardInner = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Width = 340
        };
        cardInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        cardInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        cardInner.Controls.Add(new Label
        {
            Text = "Ingresá la clave de acceso para continuar.",
            AutoSize = true,
            Font = UiTheme.UiFont(9.5f),
            ForeColor = UiTheme.TextMuted,
            Margin = new Padding(0, 0, 0, 10),
            MaximumSize = new Size(340, 0)
        }, 0, 0);

        _txtPassword = new TextBox
        {
            Width = 340,
            Height = 30,
            UseSystemPasswordChar = true,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        UiTheme.StyleTextBox(_txtPassword, compact: true);
        _txtPassword.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                TryEnter();
                return;
            }

            if (e.KeyCode == Keys.CapsLock)
                BeginInvoke(UpdateCapsLockHint);
        };
        _txtPassword.KeyUp += (_, e) =>
        {
            if (e.KeyCode == Keys.CapsLock)
                UpdateCapsLockHint();
        };
        _txtPassword.Enter += (_, _) => UpdateCapsLockHint();
        _txtPassword.Leave += (_, _) => _lblCapsLock.Visible = false;
        cardInner.Controls.Add(_txtPassword, 0, 1);

        _lblCapsLock = new Label
        {
            Text = "Mayúsculas activadas",
            AutoSize = true,
            Visible = false,
            ForeColor = Color.FromArgb(168, 92, 18),
            Font = UiTheme.UiFont(8.75f),
            Margin = new Padding(0, 6, 0, 0),
            MaximumSize = new Size(340, 0)
        };
        cardInner.Controls.Add(_lblCapsLock, 0, 2);

        _lblStatus = new Label
        {
            Text = " ",
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = UiTheme.Danger,
            Font = UiTheme.UiFont(9f),
            Margin = new Padding(0, 4, 0, 0),
            MaximumSize = new Size(340, 0)
        };
        cardInner.Controls.Add(_lblStatus, 0, 3);

        var btnRow = new Panel
        {
            Height = 32,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0),
            MinimumSize = new Size(340, 32)
        };

        _btnEnter = new Button
        {
            Text = "Ingresar",
            Size = new Size(116, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        StyleLoginPrimaryButton(_btnEnter);
        _btnEnter.Click += (_, _) => TryEnter();
        btnRow.Controls.Add(_btnEnter);
        btnRow.Resize += (_, _) =>
        {
            _btnEnter.Top = 0;
            _btnEnter.Left = Math.Max(0, btnRow.ClientSize.Width - _btnEnter.Width);
        };
        cardInner.Controls.Add(btnRow, 0, 4);

        return CardSection.CreateInlineBarTop(cardInner, new Padding(14, 12, 14, 10));
    }

    private void UpdateCapsLockHint()
    {
        _lblCapsLock.Visible = _txtPassword.Focused && Control.IsKeyLocked(Keys.CapsLock);
    }

    private static Image? LoadBrandIcon(int size)
    {
        var icon = AppIcon.Get();
        if (icon is null)
            return null;

        using var sized = new Icon(icon, new Size(size, size));
        return sized.ToBitmap();
    }

    private static void StyleLoginPrimaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = UiTheme.Connect;
        b.ForeColor = Color.White;
        b.Font = UiTheme.UiFont(9.5f);
        b.Cursor = Cursors.Hand;
        b.Padding = Padding.Empty;
        b.Margin = Padding.Empty;
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.UseVisualStyleBackColor = false;
        b.UseCompatibleTextRendering = false;
        b.MouseEnter += (_, _) => b.BackColor = UiTheme.ConnectDark;
        b.MouseLeave += (_, _) => b.BackColor = UiTheme.Connect;
    }

    private void TryEnter()
    {
        if (AppAccessGate.TryVerify(_txtPassword.Text))
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        _attempts++;
        _txtPassword.Clear();
        _txtPassword.Focus();

        if (_attempts >= MaxAttempts)
        {
            MessageBox.Show(this,
                "Demasiados intentos fallidos.\n\nEsta herramienta es de uso interno ST2.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        var left = MaxAttempts - _attempts;
        _lblStatus.Text = left == 1
            ? "Clave incorrecta. Queda 1 intento."
            : $"Clave incorrecta. Quedan {left} intentos.";
        _lblStatus.ForeColor = UiTheme.Danger;
    }
}
