using System.Drawing;
using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

/// <summary>Pantalla inicial: conexión SQL y elegir Backup o Restaurar.</summary>
public sealed class HomeForm : Form
{
    private TextBox _txtServer = null!;
    private Button _btnConnect = null!;
    private Label _lblConnStatus = null!;
    private OrangeProgressBar _connectProgress = null!;
    private Button _btnBackup = null!;
    private Button _btnRestore = null!;
    private NavRowControl _btnQuery = null!;
    private NavRowControl _btnTrace = null!;
    private bool _connecting;
    private CancellationTokenSource? _connectCts;
    private System.Windows.Forms.Timer? _connectElapsedTimer;
    private DateTime _connectStartedAt;
    private string _connectLastStep = "";

    public HomeForm()
    {
        Text = "ST2 · Herramientas SQL";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.UiFont();
        BackColor = UiTheme.AppBack;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(480, 520);
        MinimumSize = ClientSize;
        MaximumSize = new Size(520, 560);
        TryApplyIcon();
        BuildUi();
        AppSession.ConnectionChanged += OnSessionConnectionChanged;
        FormClosed += (_, _) =>
        {
            AppSession.ConnectionChanged -= OnSessionConnectionChanged;
            _connectCts?.Cancel();
            _connectElapsedTimer?.Stop();
            _connectElapsedTimer?.Dispose();
        };

        var fromReg = BejermanRegistry.TryGetServerOdbc();
        if (!string.IsNullOrWhiteSpace(fromReg))
            _txtServer.Text = fromReg;

        RefreshConnectionUi();

        UiTheme.BindFixedFormScreenFit(this);
    }

    private void TryApplyIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
                Icon = new Icon(icoPath);
        }
        catch
        {
            // sin icono
        }
    }

    private void BuildUi()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.AppBack
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.AppHeaderHeight));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));

        var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 20, 0) };
        header.Paint += (_, e) => UiTheme.PaintAppHeaderBackground(e.Graphics, header.ClientRectangle);
        var titleLay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        titleLay.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        titleLay.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));
        titleLay.Controls.Add(new Label
        {
            Text = "ST2",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.White,
            Font = UiTheme.UiFont(18f, FontStyle.Bold),
            BackColor = Color.Transparent
        }, 0, 0);
        titleLay.Controls.Add(new Label
        {
            Text = "Backup, scripts, consultas y traza SQL para Sistemas Bejerman",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(255, 220, 200),
            Font = UiTheme.UiFont(9.25f),
            BackColor = Color.Transparent
        }, 0, 1);
        header.Controls.Add(titleLay);
        shell.Controls.Add(header, 0, 0);

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(22, 16, 22, 10)
        };
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));

        stack.Controls.Add(BuildConnectSection(), 0, 0);
        stack.Controls.Add(new Label
        {
            Text = "Elija la opción",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(10f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 1);

        _btnBackup = new Button { Text = "Backup de Bases", Dock = DockStyle.Fill };
        UiTheme.StyleMenuButton(_btnBackup, primary: true);
        _btnBackup.Margin = new Padding(0, 0, 0, 6);
        _btnBackup.Click += (_, _) => OpenBackup();

        _btnRestore = new Button { Text = "Restaurar Bases", Dock = DockStyle.Fill };
        UiTheme.StyleMenuButton(_btnRestore, primary: false);
        _btnRestore.Margin = new Padding(0);
        _btnRestore.Click += (_, _) => OpenRestore();

        stack.Controls.Add(_btnBackup, 0, 2);
        stack.Controls.Add(_btnRestore, 0, 3);

        var toolsHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Padding = new Padding(4, 4, 4, 4)
        };
        toolsHost.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        toolsHost.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        _btnQuery = new NavRowControl
        {
            Dock = DockStyle.Fill,
            Title = "Aplicar Script/Query",
            Subtitle = "Consultas, scripts frecuentes y análisis",
            Margin = new Padding(0, 0, 0, 4)
        };
        _btnQuery.RowClick += (_, _) => OpenQuery();

        _btnTrace = new NavRowControl
        {
            Dock = DockStyle.Fill,
            Title = "Traza SQL",
            Subtitle = "Errores y movimientos en vivo, con filtro por equipo e IA",
            Margin = new Padding(0)
        };
        _btnTrace.RowClick += (_, _) => OpenTrace();

        toolsHost.Controls.Add(_btnQuery, 0, 0);
        toolsHost.Controls.Add(_btnTrace, 0, 1);
        stack.Controls.Add(
            CardSection.CreateFill("Herramientas", toolsHost, new Padding(0, 10, 0, 4)),
            0, 4);

        shell.Controls.Add(stack, 0, 1);

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(0, 0, 10, 0)
        };
        var signature = new LinkLabel
        {
            Text = "x @LG",
            AutoSize = true,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            LinkColor = Color.FromArgb(120, 126, 134),
            ActiveLinkColor = Color.FromArgb(70, 76, 84),
            VisitedLinkColor = Color.FromArgb(120, 126, 134),
            DisabledLinkColor = Color.FromArgb(120, 126, 134),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = UiTheme.UiFont(8.5f),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        signature.Links.Add(0, signature.Text.Length, "https://www.tolei.dev");
        signature.LinkClicked += (_, e) =>
        {
            if (e.Link is null)
                return;
            e.Link.Visited = true;
            var url = e.Link.LinkData as string ?? "https://www.tolei.dev";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // sin navegador predeterminado
            }
        };
        // Dock=Right dentro del panel (con su Padding) la deja siempre bien ubicada,
        // sin depender de matemática manual en el evento Resize (esa era la causa de
        // que a veces no se viera / quedara mal posicionada).
        footer.Controls.Add(signature);
        shell.Controls.Add(footer, 0, 2);

        Controls.Add(shell);
    }

    private Control BuildConnectSection()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 6f));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96f));

        bar.Controls.Add(new Label
        {
            Text = "SQL Server:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.75f)
        }, 0, 0);

        _txtServer = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "SERVIDOR\\INSTANCIA",
            Margin = new Padding(0, 2, 6, 2)
        };
        UiTheme.StyleTextBox(_txtServer, compact: true);
        bar.Controls.Add(_txtServer, 1, 0);

        _btnConnect = new Button { Text = "Conectar", Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        UiTheme.StyleConnectButtonForBar(_btnConnect);
        _btnConnect.Click += async (_, _) => await OnConnectButtonClickAsync().ConfigureAwait(true);
        bar.Controls.Add(_btnConnect, 2, 0);
        outer.Controls.Add(bar, 0, 0);

        _connectProgress = new OrangeProgressBar
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            MaxBarHeight = 5,
            Visible = false
        };
        outer.Controls.Add(_connectProgress, 0, 1);

        _lblConnStatus = UiTheme.CreateConnectionStatusLabel(centered: true);
        outer.Controls.Add(_lblConnStatus, 0, 2);

        return CardSection.CreateInlineBarFill(outer);
    }

    private async Task OnConnectButtonClickAsync()
    {
        if (_connecting)
        {
            _connectCts?.Cancel();
            return;
        }

        await ConnectAsync().ConfigureAwait(true);
    }

    private async Task ConnectAsync()
    {
        if (_connecting)
            return;

        _connecting = true;
        _connectCts = new CancellationTokenSource();
        _connectStartedAt = DateTime.UtcNow;
        _connectLastStep = "Conectando…";

        _btnConnect.Text = "Cancelar";
        _connectProgress.Visible = true;
        _connectProgress.Marquee = true;
        _lblConnStatus.ForeColor = UiTheme.TextMuted;
        _lblConnStatus.Text = _connectLastStep;

        _connectElapsedTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _connectElapsedTimer.Tick += (_, _) => UpdateConnectStatusText();
        _connectElapsedTimer.Start();

        var attemptedSteps = new List<string>();
        var progress = new Progress<string>(msg =>
        {
            attemptedSteps.Add(msg);
            _connectLastStep = msg;
            UpdateConnectStatusText();
        });

        try
        {
            await AppSession.ConnectAsync(_txtServer.Text, progress, _connectCts.Token).ConfigureAwait(true);
            RefreshConnectionUi();
        }
        catch (Exception ex) when (ex is OperationCanceledException || (_connectCts?.IsCancellationRequested ?? false))
        {
            AppSession.Disconnect();
            RefreshConnectionUi();
            _lblConnStatus.Text = "● Conexión cancelada";
            _lblConnStatus.ForeColor = UiTheme.TextMuted;
        }
        catch (Exception ex)
        {
            AppSession.Disconnect();
            RefreshConnectionUi();
            var elapsed = DateTime.UtcNow - _connectStartedAt;
            var detalle = attemptedSteps.Count > 0
                ? string.Join("\n", attemptedSteps)
                : ex.Message;
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError("No se pudo conectar al servidor SQL.", ex)
                    + $"\n\nTiempo transcurrido: {elapsed.TotalSeconds:0.0} s"
                    + "\n\nPasos intentados:\n" + detalle,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connectElapsedTimer?.Stop();
            _connectElapsedTimer?.Dispose();
            _connectElapsedTimer = null;
            _connectCts?.Dispose();
            _connectCts = null;
            _connecting = false;
            _btnConnect.Text = "Conectar";
            _connectProgress.Marquee = false;
            _connectProgress.Visible = false;
        }
    }

    private void UpdateConnectStatusText()
    {
        if (!_connecting)
            return;
        var elapsed = DateTime.UtcNow - _connectStartedAt;
        _lblConnStatus.Text = $"{_connectLastStep} ({elapsed.TotalSeconds:0} s)";
    }

    private void OnSessionConnectionChanged() =>
        BeginInvoke(RefreshConnectionUi);

    private void RefreshConnectionUi()
    {
        UiTheme.SetConnectionStatusLabel(_lblConnStatus, AppSession.IsConnected, AppSession.ServerName);
        if (AppSession.IsConnected && !string.IsNullOrWhiteSpace(AppSession.ServerName))
            _txtServer.Text = AppSession.ServerName;
    }

    private void OpenBackup()
    {
        if (!AppSession.IsConnected)
        {
            MessageBox.Show(this,
                "Debe conectarse al servidor SQL antes de continuar.\n\nIngrese el servidor y pulse «Conectar».",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Hide();
        using var f = new Form1();
        f.ShowDialog(this);
        Show();
    }

    private void OpenRestore()
    {
        if (!AppSession.IsConnected)
        {
            MessageBox.Show(this,
                "Debe conectarse al servidor SQL antes de continuar.\n\nIngrese el servidor y pulse «Conectar».",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Hide();
        using var f = new RestoreForm();
        f.ShowDialog(this);
        Show();
    }

    private void OpenQuery()
    {
        if (!AppSession.IsConnected)
        {
            MessageBox.Show(this,
                "Debe conectarse al servidor SQL antes de continuar.\n\nIngrese el servidor y pulse «Conectar».",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Hide();
        using var f = new QueryForm();
        f.ShowDialog(this);
        Show();
    }

    private void OpenTrace()
    {
        if (!AppSession.IsConnected)
        {
            MessageBox.Show(this,
                "Debe conectarse al servidor SQL antes de continuar.\n\nIngrese el servidor y pulse «Conectar».",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // La traza se muestra sin ser modal (Show, no ShowDialog): un diálogo modal
        // se cierra solo si el usuario lo minimiza (WinForms lo hace a propósito para
        // evitar quedarse sin ninguna ventana visible), y acá justamente se quiere
        // poder minimizarla y seguir trabajando mientras corre en segundo plano.
        Hide();
        var f = new TraceForm();
        f.FormClosed += (_, _) =>
        {
            f.Dispose();
            Show();
        };
        f.Show(this);
        // Al crearse desde un evento (clic en "Traza SQL") que además esconde esta
        // ventana, Windows a veces no le da foco/primer plano a la ventana nueva por
        // su protección anti "robo de foco". Eso deja la barra de título dibujada
        // como "inactiva" (botones grises tenues) aunque funcionen bien: forzamos
        // que tome foco para que se vea (y comporte) como ventana activa normal.
        f.Activate();
    }
}
