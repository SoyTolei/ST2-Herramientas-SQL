using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using SBBackup.Models;
using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

/// <summary>
/// Monitor de traza SQL (Extended Events), categorías tipo Profiler.
/// </summary>
public sealed class TraceForm : Form
{
    private readonly SqlTraceCoordinator _trace = new();
    private readonly QueryAiExplainer _ai = new();
    private readonly List<SqlTraceEvent> _captured = [];
    private readonly BindingList<SqlTraceEvent> _events = [];
    private readonly Dictionary<string, string> _aiCache = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer _pollTimer;

    private Label _lblConnStatus = null!;
    private Label _lblStatus = null!;
    private Panel _statusDot = null!;
    private Label _lblAiTitle = null!;
    private Button _btnStart = null!;
    private Button _btnPause = null!;
    private Button _btnStop = null!;
    private Button _btnClear = null!;
    private Button _btnExport = null!;
    private Label _btnExpandAi = null!;
    private CheckBox _chkErrors = null!;
    private CheckBox _chkSecurity = null!;
    private CheckBox _chkSessions = null!;
    private CheckBox _chkTsql = null!;
    private TextBox _txtFilterHost = null!;
    private DataGridView _grid = null!;
    private TextBox _txtDetail = null!;
    private Label _lblAiBody = null!;
    private Panel _aiScroll = null!;
    private Panel _aiPanel = null!;
    private bool _busy;
    private bool _aiErrorMode;
    private bool _polling;
    private bool _aiHasResult;
    private string? _aiResultKey;

    public TraceForm()
    {
        Text = "ST2 · Traza SQL";
        UiTheme.ApplyDpiAwareScaling(this);
        Font = UiTheme.UiFont();
        BackColor = UiTheme.AppBack;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        // Tamaño forzado desde el ctor: evita ventanas “chatas” donde no se ve la grilla.
        Size = new Size(1060, 760);
        MinimumSize = new Size(960, 680);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _pollTimer.Tick += async (_, _) => await PollOnceAsync().ConfigureAwait(true);

        BuildUi();
        FormClosed += (_, _) =>
        {
            _pollTimer.Stop();
            _ai.Dispose();
        };
        FormClosing += async (_, _) =>
        {
            _pollTimer.Stop();
            if (_trace.HasActiveSession && !string.IsNullOrEmpty(AppSession.ConnectionString))
            {
                try
                {
                    await _trace.StopAsync(AppSession.ConnectionString, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch
                {
                }
            }
        };
        Load += (_, _) => EnsureComfortableSize();
        Shown += (_, _) =>
        {
            EnsureComfortableSize();
            SetAiPanelState(errorMode: false, title: "Análisis IA",
                text: "Seleccioná un error en la grilla.\r\n\r\nDespués hacé clic en este bloque para analizarlo con IA.");
            RefreshConnectionUi();
            UpdateButtons();
        };
        DpiChanged += (_, _) => EnsureComfortableSize();
    }

    private void EnsureComfortableSize()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var maxW = Math.Max(UiTheme.Scale(this, 700), area.Width - UiTheme.Scale(this, 40));
        var maxH = Math.Max(UiTheme.Scale(this, 500), area.Height - UiTheme.Scale(this, 40));

        // Si el escalado de Windows agrandó el tamaño mínimo más allá de la pantalla,
        // lo reducimos primero.
        if (MinimumSize.Width > maxW || MinimumSize.Height > maxH)
            MinimumSize = new Size(Math.Min(MinimumSize.Width, maxW), Math.Min(MinimumSize.Height, maxH));

        var minComfortW = UiTheme.Scale(this, 900);
        var minComfortH = UiTheme.Scale(this, 620);
        var w = Math.Clamp(ClientSize.Width, Math.Min(minComfortW, maxW), maxW);
        var h = Math.Clamp(ClientSize.Height, Math.Min(minComfortH, maxH), maxH);
        if (ClientSize.Width != w || ClientSize.Height != h)
            ClientSize = new Size(w, h);
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void BuildUi()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.SubFormHeaderHeight));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        shell.Controls.Add(UiTheme.CreateSubFormHeader("ST2 · Traza SQL", (_, _) => Close()), 0, 0);

        // Layout rígido: controles / grilla / detalle. Sin SplitContainer (fallaba a altura 0).
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(12, 8, 12, 10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));   // conexión
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142f));  // categorías + filtros + botones
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));    // grilla
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));    // detalle + IA
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));   // status

        _lblConnStatus = UiTheme.CreateConnectionStatusLabel();
        root.Controls.Add(_lblConnStatus, 0, 0);
        root.Controls.Add(BuildControlsPanel(), 0, 1);
        root.Controls.Add(BuildGridSection(), 0, 2);
        root.Controls.Add(BuildDetailSection(), 0, 3);
        root.Controls.Add(BuildStatusBar(), 0, 4);

        shell.Controls.Add(root, 0, 1);
        Controls.Add(shell);
    }

    private Control BuildStatusBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _statusDot = new Panel
        {
            Size = new Size(10, 10),
            Margin = new Padding(2, 8, 0, 0),
            Anchor = AnchorStyles.Left
        };
        _statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = GetStatusDotColor();
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 0, 0, 9, 9);
            using var pen = new Pen(Color.FromArgb(40, 0, 0, 0));
            e.Graphics.DrawEllipse(pen, 0, 0, 9, 9);
        };

        _lblStatus = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9f),
            Text = "Detenida. Elegí categorías y pulsá «Iniciar traza»."
        };

        bar.Controls.Add(_statusDot, 0, 0);
        bar.Controls.Add(_lblStatus, 1, 0);
        return bar;
    }

    private Color GetStatusDotColor()
    {
        var text = _lblStatus?.Text ?? "";
        if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No se pudo", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(200, 50, 50);
        if (_trace.IsPaused)
            return Color.FromArgb(210, 150, 35);
        if (_trace.IsRunning)
            return Color.FromArgb(16, 150, 95);
        return Color.FromArgb(160, 168, 176);
    }

    private void RefreshStatusDot() => _statusDot?.Invalidate();

    private Panel BuildControlsPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 4)
        };
        panel.Paint += (_, e) => PaintRoundedBorder(e.Graphics, panel.ClientRectangle);

        var lay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 2, 0, 0)
        };
        // Defaults: errores + TSQL (movimientos).
        _chkErrors = MakeCategoryCheck("Errors and Warnings", true);
        _chkTsql = MakeCategoryCheck("TSQL", true);
        _chkSecurity = MakeCategoryCheck("Security Audit", false);
        _chkSessions = MakeCategoryCheck("Sessions", false);
        filters.Controls.Add(_chkErrors);
        filters.Controls.Add(_chkTsql);
        filters.Controls.Add(_chkSecurity);
        filters.Controls.Add(_chkSessions);
        lay.Controls.Add(filters, 0, 0);

        var scope = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 2, 0, 0)
        };
        scope.Controls.Add(new Label
        {
            Text = "Nombre del equipo:",
            AutoSize = true,
            Margin = new Padding(0, 6, 6, 0),
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(8.5f)
        });
        _txtFilterHost = new TextBox
        {
            Width = 180,
            Height = 24,
            Margin = new Padding(0, 2, 8, 0),
            PlaceholderText = "ej. W11880",
            Font = UiTheme.UiFont(9f)
        };
        _txtFilterHost.TextChanged += (_, _) => RefreshFilteredView();
        scope.Controls.Add(_txtFilterHost);
        lay.Controls.Add(scope, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 4),
            Margin = Padding.Empty
        };
        _btnStart = MakeActionButton("Iniciar traza", 128, Color.FromArgb(16, 140, 90), Color.FromArgb(10, 110, 70));
        _btnStart.Click += async (_, _) => await StartAsync().ConfigureAwait(true);
        _btnPause = MakeActionButton("Pausar", 96, Color.FromArgb(200, 145, 40), Color.FromArgb(170, 120, 25));
        _btnPause.Click += async (_, _) => await TogglePauseAsync().ConfigureAwait(true);
        _btnStop = MakeActionButton("Detener", 96, Color.FromArgb(200, 50, 50), Color.FromArgb(160, 35, 35));
        _btnStop.Click += async (_, _) => await StopAsync().ConfigureAwait(true);
        _btnClear = MakeOutlineButton("Limpiar", 92);
        _btnClear.Click += (_, _) =>
        {
            _captured.Clear();
            _events.Clear();
            _aiCache.Clear();
            _txtDetail.Text = "";
            SetAiPanelState(errorMode: false, title: "Análisis IA",
                text: "Seleccioná un error en la grilla.\r\n\r\nDespués hacé clic en este bloque para analizarlo con IA.");
            SetStatus("Lista vacía.");
            UpdateButtons();
        };
        _btnExport = MakeOutlineButton("Exportar…", 108);
        _btnExport.Click += (_, _) => ExportReport();
        actions.Controls.Add(_btnStart);
        actions.Controls.Add(_btnPause);
        actions.Controls.Add(_btnStop);
        actions.Controls.Add(_btnClear);
        actions.Controls.Add(_btnExport);
        lay.Controls.Add(actions, 0, 2);

        panel.Controls.Add(lay);
        return panel;
    }

    private Control BuildGridSection()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Margin = new Padding(0, 2, 0, 4),
            MinimumSize = new Size(0, 220)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        outer.Controls.Add(new Label
        {
            Text = "Eventos capturados (tiempo real)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.SectionTitleFont(),
            Padding = new Padding(2, 0, 0, 2)
        }, 0, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            DataSource = _events,
            BackgroundColor = UiTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle
        };
        UiTheme.StyleDataGrid(_grid);
        // Un punto más chico que el grilla estándar: más filas visibles en la traza.
        var gridFont = UiTheme.UiFont(8.5f);
        _grid.DefaultCellStyle.Font = gridFont;
        _grid.AlternatingRowsDefaultCellStyle.Font = gridFont;
        _grid.ColumnHeadersDefaultCellStyle.Font = UiTheme.UiFont(8.25f, FontStyle.Bold);
        _grid.RowTemplate.Height = 22;
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.Timestamp), "Hora", 11, 90, "HH:mm:ss.fff"));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.Category), "Categoría", 14, 100));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.EventName), "Evento", 12, 80));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.Severity), "Sev", 5, 36));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.DurationMs), "ms", 5, 36));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.DatabaseName), "Base", 11, 70));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.ClientHost), "PC", 10, 70));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.LoginName), "Login", 11, 70));
        _grid.Columns.Add(Col(nameof(SqlTraceEvent.Message), "Mensaje / SQL", 28, 110));
        _grid.SelectionChanged += (_, _) => ShowSelectedDetail();
        _grid.CellFormatting += Grid_CellFormatting;

        var gridHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(1),
            BorderStyle = BorderStyle.FixedSingle
        };
        gridHost.Controls.Add(_grid);
        outer.Controls.Add(gridHost, 0, 1);
        return outer;
    }

    private Control BuildDetailSection()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.AppBack,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 0),
            MinimumSize = new Size(0, 220)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));   // análisis IA (bloque clicable)
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));   // detalle técnico

        outer.Controls.Add(new Label
        {
            Text = "Detalle del evento",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(8.5f, FontStyle.Bold),
            Padding = new Padding(2, 0, 0, 0)
        }, 0, 0);

        _aiPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 2, 0, 6),
            Cursor = Cursors.Hand
        };
        _aiPanel.Paint += (_, e) => PaintAiPanel(e.Graphics, _aiPanel.ClientRectangle, _aiErrorMode);
        _aiPanel.Click += async (_, _) => await OnAiBlockClickAsync().ConfigureAwait(true);

        var aiLay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        aiLay.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        aiLay.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var aiHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        aiHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        aiHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
        _lblAiTitle = new Label
        {
            Text = "Análisis IA",
            Dock = DockStyle.Fill,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        _lblAiTitle.Click += async (_, _) => await OnAiBlockClickAsync().ConfigureAwait(true);
        _btnExpandAi = new Label
        {
            Text = "Ampliar",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = UiTheme.UiFont(8.5f, FontStyle.Underline),
            ForeColor = Color.FromArgb(91, 74, 183),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Padding(0, 0, 4, 0),
            Enabled = false
        };
        _btnExpandAi.Click += (_, _) => ShowAiExpanded();
        _btnExpandAi.MouseEnter += (_, _) =>
        {
            if (_btnExpandAi.Enabled)
                _btnExpandAi.ForeColor = Color.FromArgb(68, 54, 153);
        };
        _btnExpandAi.MouseLeave += (_, _) =>
        {
            _btnExpandAi.ForeColor = _btnExpandAi.Enabled
                ? Color.FromArgb(91, 74, 183)
                : UiTheme.TextMuted;
        };
        aiHeader.Controls.Add(_lblAiTitle, 0, 0);
        aiHeader.Controls.Add(_btnExpandAi, 1, 0);

        _aiScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Padding = new Padding(4, 6, 4, 4),
            Cursor = Cursors.Hand
        };
        _aiScroll.Click += async (_, _) => await OnAiBlockClickAsync().ConfigureAwait(true);
        _aiScroll.Resize += (_, _) => FitAiBodyWidth();

        _lblAiBody = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.5f),
            Padding = new Padding(2, 2, 8, 8),
            Cursor = Cursors.Hand,
            UseMnemonic = false
        };
        _lblAiBody.Click += async (_, _) => await OnAiBlockClickAsync().ConfigureAwait(true);
        _aiScroll.Controls.Add(_lblAiBody);

        aiLay.Controls.Add(aiHeader, 0, 0);
        aiLay.Controls.Add(_aiScroll, 0, 1);
        _aiPanel.Controls.Add(aiLay);
        outer.Controls.Add(_aiPanel, 0, 1);

        _txtDetail = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Font = new Font("Consolas", 8.5f),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextPrimary,
            Margin = new Padding(0, 0, 0, 0)
        };
        outer.Controls.Add(_txtDetail, 0, 2);

        SetAiPanelState(
            errorMode: false,
            title: "Análisis IA",
            text: "Seleccioná un error en la grilla.\r\n\r\nDespués hacé clic en este bloque para analizarlo con IA.");
        return outer;
    }

    private void FitAiBodyWidth()
    {
        if (_aiScroll is null || _lblAiBody is null)
            return;
        var w = Math.Max(160, _aiScroll.ClientSize.Width - 20);
        _lblAiBody.MaximumSize = new Size(w, 0);
    }

    private async Task OnAiBlockClickAsync()
    {
        if (_busy)
            return;

        var selected = _grid.CurrentRow?.DataBoundItem as SqlTraceEvent;
        if (selected is null || !IsErrorEvent(selected))
        {
            SetStatus("Seleccioná un error en la grilla para analizarlo con IA.");
            return;
        }

        // Ya analizado: no vuelve a consultar; ofrece ampliar.
        if (_aiCache.ContainsKey(selected.Key) ||
            (_aiHasResult && string.Equals(_aiResultKey, selected.Key, StringComparison.Ordinal)))
        {
            ShowAiExpanded();
            return;
        }

        await ExplainSelectedErrorAsync().ConfigureAwait(true);
    }

    private void ShowAiExpanded()
    {
        var raw = _lblAiBody.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw) || !_aiHasResult)
        {
            SetStatus("Todavía no hay un análisis para ampliar. Hacé clic en el bloque IA primero.");
            return;
        }

        var text = FormatAiParagraphs(raw);

        using var dlg = new Form
        {
            Text = "ST2 · Análisis IA",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = false,
            BackColor = UiTheme.AppBack,
            Font = UiTheme.UiFont(),
            Size = new Size(780, 560),
            MinimumSize = new Size(560, 400),
            Padding = new Padding(12)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

        var bodyBack = _aiErrorMode
            ? Color.FromArgb(245, 243, 255)
            : Color.FromArgb(244, 245, 247);
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = bodyBack,
            Padding = new Padding(18, 16, 18, 16),
            BorderStyle = BorderStyle.FixedSingle
        };
        var lbl = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(10.5f),
            Text = text,
            Padding = new Padding(2, 2, 8, 8),
            UseMnemonic = false
        };
        void FitExpandedWidth()
        {
            var w = Math.Max(280, scroll.ClientSize.Width - 28);
            lbl.MaximumSize = new Size(w, 0);
        }
        scroll.Resize += (_, _) => FitExpandedWidth();
        scroll.Controls.Add(lbl);
        FitExpandedWidth();
        root.Controls.Add(scroll, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };
        var btnClose = MakeOutlineButton("Cerrar", 100);
        btnClose.Click += (_, _) => dlg.Close();
        var btnCopy = MakeOutlineButton("Copiar", 100);
        btnCopy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                SetStatus("Análisis IA copiado.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(dlg,
                    UserMessageSpanish.FriendlyError("No se pudo copiar al portapapeles.", ex),
                    dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        actions.Controls.Add(btnClose);
        actions.Controls.Add(btnCopy);
        root.Controls.Add(actions, 0, 1);

        dlg.Controls.Add(root);
        dlg.Shown += (_, _) => FitExpandedWidth();
        dlg.ShowDialog(this);
    }

    /// <summary>Normaliza el texto de IA a párrafos legibles (como en la preview).</summary>
    private static string FormatAiParagraphs(string text)
    {
        var t = (text ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (t.Length == 0)
            return "";

        while (t.Contains("\n\n\n", StringComparison.Ordinal))
            t = t.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);

        // Si vino todo en líneas sueltas sin párrafos, cada salto pasa a párrafo.
        if (!t.Contains("\n\n", StringComparison.Ordinal) && t.Contains('\n'))
            t = t.Replace("\n", "\n\n", StringComparison.Ordinal);

        return t.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private void SetAiPanelState(bool errorMode, string title, string text, bool isResult = false, string? resultKey = null)
    {
        _aiErrorMode = errorMode;
        _aiHasResult = isResult;
        _aiResultKey = isResult ? resultKey : null;
        var bg = errorMode
            ? Color.FromArgb(245, 243, 255)
            : Color.FromArgb(244, 245, 247);
        var titleFg = errorMode
            ? Color.FromArgb(68, 54, 153)
            : UiTheme.TextMuted;
        var textFg = errorMode
            ? UiTheme.TextPrimary
            : UiTheme.TextMuted;

        _aiPanel.BackColor = bg;
        _aiScroll.BackColor = bg;
        _lblAiTitle.Text = title;
        _lblAiTitle.ForeColor = titleFg;
        _lblAiBody.ForeColor = textFg;
        _lblAiBody.Text = isResult ? FormatAiParagraphs(text) : text;
        FitAiBodyWidth();
        _aiPanel.Invalidate();
        UpdateAiBlockCursor();
    }

    private void UpdateAiBlockCursor()
    {
        var selected = _grid.CurrentRow?.DataBoundItem as SqlTraceEvent;
        var isError = selected is not null && IsErrorEvent(selected);
        var alreadyDone = selected is not null && _aiCache.ContainsKey(selected.Key);
        var canExplain = !_busy && _ai.IsConfigured && isError && !alreadyDone;
        var cursor = canExplain || (isError && alreadyDone) ? Cursors.Hand : Cursors.Default;
        _aiPanel.Cursor = cursor;
        _aiScroll.Cursor = cursor;
        _lblAiTitle.Cursor = cursor;
        _lblAiBody.Cursor = cursor;
        _btnExpandAi.Enabled = _aiHasResult && !_busy;
        _btnExpandAi.ForeColor = _btnExpandAi.Enabled
            ? Color.FromArgb(91, 74, 183)
            : UiTheme.TextMuted;
        _btnExpandAi.Cursor = _btnExpandAi.Enabled ? Cursors.Hand : Cursors.Default;
        _btnExpandAi.Font = UiTheme.UiFont(8.5f, _btnExpandAi.Enabled ? FontStyle.Underline : FontStyle.Regular);
    }

    private static void PaintAiPanel(Graphics g, Rectangle client, bool errorMode)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, client.Width - 1, client.Height - 1);
        if (r.Width < 4)
            return;

        var bg = errorMode
            ? Color.FromArgb(245, 243, 255)
            : Color.FromArgb(244, 245, 247);
        var accent = errorMode
            ? Color.FromArgb(91, 74, 183)
            : Color.FromArgb(190, 196, 204);

        using var path = UiTheme.RoundedRectangle(r, 8);
        using var fill = new SolidBrush(bg);
        g.FillPath(fill, path);
        using var pen = new Pen(accent, errorMode ? 1.5f : 1f);
        g.DrawPath(pen, path);
        using var stripe = new SolidBrush(accent);
        g.FillRectangle(stripe, 0, 0, 4, client.Height);
    }

    private static void PaintRoundedBorder(Graphics g, Rectangle client)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, client.Width - 1, client.Height - 1);
        if (r.Width < 4)
            return;
        using var path = UiTheme.RoundedRectangle(r, 10);
        using var pen = new Pen(UiTheme.Border);
        g.DrawPath(pen, path);
    }

    private static DataGridViewTextBoxColumn Col(
        string prop, string header, float fill, int min, string? format = null)
    {
        var c = new DataGridViewTextBoxColumn
        {
            DataPropertyName = prop,
            HeaderText = header,
            FillWeight = fill,
            MinimumWidth = min
        };
        if (format is not null)
            c.DefaultCellStyle = new DataGridViewCellStyle { Format = format };
        return c;
    }

    private static CheckBox MakeCategoryCheck(string text, bool checkedDefault)
    {
        var chk = new CheckBox
        {
            Text = text,
            Checked = checkedDefault,
            AutoSize = true,
            Margin = new Padding(0, 2, 12, 2)
        };
        UiTheme.StyleCheckBox(chk);
        return chk;
    }

    private static Button MakeActionButton(string text, int width, Color normal, Color hover)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(width, 28),
            MinimumSize = new Size(width, 28),
            MaximumSize = new Size(width, 28),
            AutoSize = false,
            Margin = new Padding(0, 0, 8, 0),
            Padding = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = normal,
            ForeColor = Color.White,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseCompatibleTextRendering = true,
            TextAlign = ContentAlignment.MiddleCenter
        };
        b.FlatAppearance.BorderSize = 0;
        var n = normal;
        var h = hover;
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = h; };
        b.MouseLeave += (_, _) => { if (b.Enabled) b.BackColor = n; };
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? n : Color.FromArgb(180, 180, 180);
            b.ForeColor = Color.White;
        };
        return b;
    }

    private static Button MakeOutlineButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(width, 28),
            MinimumSize = new Size(width, 28),
            MaximumSize = new Size(width, 28),
            AutoSize = false,
            Margin = new Padding(0, 0, 8, 0),
            Padding = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseCompatibleTextRendering = true,
            TextAlign = ContentAlignment.MiddleCenter
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = UiTheme.Border;
        b.MouseEnter += (_, _) =>
        {
            if (!b.Enabled)
                return;
            b.BackColor = UiTheme.PrimarySoft;
            b.FlatAppearance.BorderColor = UiTheme.Primary;
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = UiTheme.Surface;
            b.FlatAppearance.BorderColor = UiTheme.Border;
        };
        b.EnabledChanged += (_, _) =>
        {
            b.ForeColor = b.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted;
            b.FlatAppearance.BorderColor = UiTheme.Border;
            b.BackColor = UiTheme.Surface;
        };
        return b;
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _events.Count || e.CellStyle is null)
            return;

        var evt = _events[e.RowIndex];
        if (evt.Category.Equals("Errors and Warnings", StringComparison.OrdinalIgnoreCase)
            && evt.Severity is >= 11)
        {
            e.CellStyle.ForeColor = UiTheme.IncludeNoFg;
            e.CellStyle.SelectionForeColor = UiTheme.IncludeNoFg;
        }
        else if (evt.Category.Equals("Errors and Warnings", StringComparison.OrdinalIgnoreCase))
        {
            e.CellStyle.ForeColor = Color.FromArgb(180, 110, 20);
            e.CellStyle.SelectionForeColor = Color.FromArgb(180, 110, 20);
        }

        if (_grid.Columns[e.ColumnIndex].DataPropertyName == nameof(SqlTraceEvent.Message)
            && string.IsNullOrWhiteSpace(Convert.ToString(e.Value))
            && !string.IsNullOrWhiteSpace(evt.SqlText))
        {
            e.Value = evt.SqlText;
            e.FormattingApplied = true;
        }
    }

    private void ShowSelectedDetail()
    {
        if (_grid.CurrentRow?.DataBoundItem is not SqlTraceEvent evt)
        {
            _txtDetail.Text = "";
            SetAiPanelState(errorMode: false, title: "Análisis IA",
                text: "Seleccioná un error en la grilla.\r\n\r\nDespués hacé clic en este bloque para analizarlo con IA.");
            UpdateButtons();
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Hora: {evt.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Categoría: {evt.Category}  ·  Evento: {evt.EventName}");
        if (evt.Severity is not null)
            sb.AppendLine($"Severidad: {evt.Severity}  ·  Error: {evt.ErrorNumber}");
        if (evt.DurationMs is not null)
            sb.AppendLine($"Duración: {evt.DurationMs} ms");
        sb.AppendLine($"SPID: {evt.SessionId}  ·  Base: {evt.DatabaseName}");
        sb.AppendLine($"Login: {evt.LoginName}  ·  App: {evt.ClientApp}  ·  Host: {evt.ClientHost}");
        if (!string.IsNullOrWhiteSpace(evt.Message))
        {
            sb.AppendLine();
            sb.AppendLine("Mensaje:");
            sb.AppendLine(evt.Message);
        }

        if (!string.IsNullOrWhiteSpace(evt.SqlText))
        {
            sb.AppendLine();
            sb.AppendLine("T-SQL:");
            sb.AppendLine(evt.SqlText);
        }

        _txtDetail.Text = sb.ToString();
        _txtDetail.SelectionStart = 0;
        _txtDetail.ScrollToCaret();

        if (IsErrorEvent(evt))
        {
            if (_aiCache.TryGetValue(evt.Key, out var cached))
            {
                SetAiPanelState(errorMode: true, title: "Análisis IA", text: cached, isResult: true, resultKey: evt.Key);
            }
            else
            {
                SetAiPanelState(errorMode: true, title: "Análisis IA · clic para explicar",
                    text: "Error seleccionado.\r\n\r\nHacé clic en este bloque para analizarlo con IA.\r\nUsá «Ampliar» cuando ya tengas el resultado.");
            }
        }
        else
        {
            SetAiPanelState(errorMode: false, title: "Análisis IA",
                text: "Este evento no es un error.\r\n\r\nEl análisis IA solo aplica a Errors and Warnings (o login fallido).");
        }

        UpdateButtons();
    }

    private static bool IsErrorEvent(SqlTraceEvent evt) =>
        evt.Category.Equals("Errors and Warnings", StringComparison.OrdinalIgnoreCase)
        || string.Equals(evt.EventName, "login_failed", StringComparison.OrdinalIgnoreCase);

    private async Task ExplainSelectedErrorAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not SqlTraceEvent evt)
        {
            MessageBox.Show(this, "Seleccioná un evento de la grilla.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!IsErrorEvent(evt))
        {
            MessageBox.Show(this,
                "La IA de errores está pensada para Events de Errors and Warnings (o login fallido).",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_ai.IsConfigured)
        {
            MessageBox.Show(this, "La explicación con IA no está configurada.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _busy = true;
        UpdateButtons();
        SetAiPanelState(errorMode: true, title: "Análisis IA · consultando…", text: "Consultando a la IA…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            // Leemos el esquema real (tablas/columnas) de la base del evento para que la IA
            // no tenga que adivinar: es la fuente de verdad de esta instalación puntual.
            var tableNames = SchemaContextBuilder.ExtractTableNames(evt.SqlText);
            var schemaContext = await SchemaContextBuilder
                .BuildAsync(AppSession.ConnectionString, evt.DatabaseName, tableNames, cts.Token)
                .ConfigureAwait(true);
            var explanation = await _ai.ExplicarErrorAsync(
                    evt.Category,
                    evt.EventName,
                    evt.Severity,
                    evt.ErrorNumber,
                    evt.DatabaseName,
                    evt.Message,
                    evt.SqlText,
                    schemaContext,
                    cts.Token)
                .ConfigureAwait(true);

            _aiCache[evt.Key] = explanation;
            SetAiPanelState(errorMode: true, title: "Análisis IA", text: explanation, isResult: true, resultKey: evt.Key);
            _aiScroll.AutoScrollPosition = new Point(0, 0);
        }
        catch (Exception ex)
        {
            var errText = "No se pudo analizar con IA:\r\n\r\n" + ex.Message +
                          "\r\n\r\nHacé clic de nuevo para reintentar.";
            // Sin cache: permite reintentar.
            SetAiPanelState(errorMode: true, title: "Análisis IA · error · clic para reintentar", text: errText);
            _lblAiBody.ForeColor = UiTheme.IncludeNoFg;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private SqlTraceOptions BuildOptions() => new()
    {
        ErrorsAndWarnings = _chkErrors.Checked,
        SecurityAudit = _chkSecurity.Checked,
        Sessions = _chkSessions.Checked,
        Tsql = _chkTsql.Checked
    };

    private async Task StartAsync()
    {
        if (_busy)
            return;
        if (!AppSession.IsConnected || string.IsNullOrEmpty(AppSession.ConnectionString))
        {
            MessageBox.Show(this, "No hay conexión al servidor SQL.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var options = BuildOptions();
        if (!options.AnySelected)
        {
            MessageBox.Show(this, "Marcá al menos una categoría de eventos.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _busy = true;
        UpdateButtons();
        SetStatus("Iniciando sesión Extended Events…");
        try
        {
            await _trace.StartAsync(AppSession.ConnectionString, options, CancellationToken.None)
                .ConfigureAwait(true);
            _pollTimer.Start();
            SetStatus("Traza activa · filtrá por «Nombre del equipo» si hace falta.");
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo iniciar la traza.");
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError(DescribeTraceStartFailure(ex), ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private static string DescribeTraceStartFailure(Exception ex)
    {
        var root = ex.GetBaseException();
        if (root is Microsoft.Data.SqlClient.SqlException sql)
        {
            // 25623: action/evento XE inválido o no disponible en esa versión de SQL.
            if (sql.Number == 25623
                || (sql.Message?.Contains("event action name", StringComparison.OrdinalIgnoreCase) ?? false)
                || (sql.Message?.Contains("event name", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return "No se pudo iniciar la traza SQL.\n\n" +
                       "Esta instancia de SQL Server no acepta alguna opción de Extended Events " +
                       "(versión antigua o acción no disponible). Probá de nuevo con la versión actualizada de ST2; " +
                       "si sigue fallando, revisá que el motor sea SQL Server 2012 o superior.";
            }

            // Permisos típicos.
            if (sql.Number is 229 or 297 or 262 or 15247)
            {
                return "No se pudo iniciar la traza SQL.\n\n" +
                       "Se necesita permiso para Extended Events " +
                       "(normalmente sysadmin o ALTER ANY EVENT SESSION).";
            }
        }

        return "No se pudo iniciar la traza SQL.\n\n" +
               "Si el detalle habla de permisos, usá un login con sysadmin " +
               "(o ALTER ANY EVENT SESSION). Si habla de un action/evento inválido, " +
               "puede ser la versión del motor SQL.";
    }

    private async Task TogglePauseAsync()
    {
        if (_busy || !_trace.HasActiveSession)
            return;
        if (string.IsNullOrEmpty(AppSession.ConnectionString))
        {
            MessageBox.Show(this, "No hay conexión al servidor SQL.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            if (_trace.IsPaused)
            {
                SetStatus("Reanudando traza…");
                await _trace.ResumeAsync(AppSession.ConnectionString, CancellationToken.None)
                    .ConfigureAwait(true);
                _pollTimer.Start();
                SetStatus($"Traza activa · {_events.Count} evento(s)");
            }
            else
            {
                SetStatus("Pausando traza…");
                _pollTimer.Stop();
                await _trace.PauseAsync(AppSession.ConnectionString, CancellationToken.None)
                    .ConfigureAwait(true);
                SetStatus($"Traza en pausa · {_events.Count} evento(s) · pulsá «Reanudar» para continuar.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo pausar/reanudar la traza.");
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError("No se pudo pausar o reanudar la traza.", ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private async Task StopAsync()
    {
        if (_busy)
            return;

        _busy = true;
        _pollTimer.Stop();
        UpdateButtons();
        SetStatus("Deteniendo traza…");
        try
        {
            if (!string.IsNullOrEmpty(AppSession.ConnectionString))
            {
                await _trace.StopAsync(AppSession.ConnectionString, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            SetStatus($"Traza detenida · {_events.Count} evento(s) listos para exportar.");
        }
        catch (Exception ex)
        {
            SetStatus("Error al detener la traza.");
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError("No se pudo detener la traza limpia en el servidor.", ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private async Task PollOnceAsync()
    {
        if (_busy || _polling || !_trace.IsRunning || string.IsNullOrEmpty(AppSession.ConnectionString))
            return;

        _polling = true;
        try
        {
            var batch = await _trace.PollAsync(AppSession.ConnectionString, CancellationToken.None)
                .ConfigureAwait(true);

            var addedVisible = 0;
            foreach (var evt in batch)
            {
                _captured.Add(evt);
                if (!PassesUserFilter(evt))
                    continue;
                _events.Add(evt);
                addedVisible++;
            }

            if (addedVisible > 0 && _grid.Rows.Count > 0)
            {
                try
                {
                    _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, _grid.Rows.Count - 1);
                }
                catch
                {
                }
            }

            SetStatus(BuildLiveStatus(addedVisible));
            UpdateButtons();
        }
        catch (Exception ex)
        {
            // No frenamos el timer: el próximo tick reintenta.
            SetStatus("Error al leer eventos (reintentando): " + TruncateStatus(ex.Message));
        }
        finally
        {
            _polling = false;
        }
    }

    private static string TruncateStatus(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "";
        const int max = 120;
        return message.Length <= max ? message : message[..max] + "…";
    }

    private bool PassesUserFilter(SqlTraceEvent evt)
    {
        var host = _txtFilterHost.Text.Trim();
        if (host.Length > 0
            && evt.ClientHost.IndexOf(host, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    private void RefreshFilteredView()
    {
        if (_txtFilterHost is null || _grid is null)
            return;

        var keepSelection = (_grid.CurrentRow?.DataBoundItem as SqlTraceEvent)?.Key;
        _events.RaiseListChangedEvents = false;
        try
        {
            _events.Clear();
            foreach (var evt in _captured)
            {
                if (PassesUserFilter(evt))
                    _events.Add(evt);
            }
        }
        finally
        {
            _events.RaiseListChangedEvents = true;
            _events.ResetBindings();
        }

        if (!string.IsNullOrEmpty(keepSelection))
        {
            for (var i = 0; i < _events.Count; i++)
            {
                if (!string.Equals(_events[i].Key, keepSelection, StringComparison.Ordinal))
                    continue;
                if (i < _grid.Rows.Count)
                {
                    _grid.ClearSelection();
                    _grid.Rows[i].Selected = true;
                    _grid.CurrentCell = _grid.Rows[i].Cells[0];
                }
                break;
            }
        }

        ShowSelectedDetail();
        SetStatus(BuildLiveStatus(0));
        UpdateButtons();
    }

    private string BuildLiveStatus(int addedVisible)
    {
        var host = _txtFilterHost.Text.Trim();
        var filterBit = host.Length > 0 ? " · equipo≈" + host : "";

        var prefix = _trace.IsPaused
            ? "Traza en pausa"
            : _trace.IsRunning
                ? "Traza activa"
                : "Detenida";

        var text =
            $"{prefix} · {_events.Count} visible(s)" +
            (_captured.Count != _events.Count ? $" / {_captured.Count} capturado(s)" : "") +
            filterBit +
            (addedVisible > 0 ? $" · +{addedVisible}" : "");
        return text;
    }

    private void ExportReport()
    {
        if (_events.Count == 0)
        {
            MessageBox.Show(this, "No hay eventos visibles para exportar (revisá el filtro de equipo).", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Exportar traza SQL",
            Filter = "CSV para Excel (*.csv)|*.csv|Texto plano / Bloc de notas (*.txt)|*.txt",
            FilterIndex = 1,
            FileName = $"ST2_TrazaSQL_{DateTime.Now:yyyyMMdd_HHmmss}",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var ext = Path.GetExtension(dlg.FileName);
            // .trc (Profiler) no aplica: usamos Extended Events, no SQL Trace clásico.
            // Exporta lo visible (con filtro de equipo aplicado).
            var content = ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? SqlTraceCoordinator.ToTxt(_events)
                : SqlTraceCoordinator.ToCsv(_events);
            File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
            SetStatus($"Exportado: {dlg.FileName}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{dlg.FileName}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError("No se pudo guardar el archivo.", ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshConnectionUi() =>
        UiTheme.SetConnectionStatusLabel(_lblConnStatus, AppSession.IsConnected, AppSession.ServerName);

    private void UpdateButtons()
    {
        var connected = AppSession.IsConnected && !_busy;
        var session = _trace.HasActiveSession;
        var paused = _trace.IsPaused;

        _btnStart.Enabled = connected && !session;
        _btnPause.Enabled = connected && session;
        _btnPause.Text = paused ? "Reanudar" : "Pausar";
        _btnStop.Enabled = connected && session;
        _btnClear.Enabled = !_busy;
        _btnExport.Enabled = !_busy && _events.Count > 0;
        UpdateAiBlockCursor();

        foreach (var chk in new[] { _chkErrors, _chkSecurity, _chkSessions, _chkTsql })
            chk.Enabled = !session;

        RefreshStatusDot();
    }

    private void SetStatus(string text)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No se pudo", StringComparison.OrdinalIgnoreCase)
            ? UiTheme.IncludeNoFg
            : UiTheme.TextMuted;
        RefreshStatusDot();
    }
}
