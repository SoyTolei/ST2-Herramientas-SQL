using System.Diagnostics;
using System.Drawing.Drawing2D;
using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

/// <summary>Restaurar bases desde archivos .bak (WITH REPLACE).</summary>
public sealed class RestoreForm : Form
{
    private readonly RestoreCoordinator _restore = new();
    private readonly AppConfig _config = AppConfig.Load(AppContext.BaseDirectory);
    private readonly List<string> _files = [];
    private readonly List<RestoreCoordinator.RestoreFilePreview> _filePreviews = [];
    private string? _connectionString;
    private bool _pinningManager;

    private Label _lblConnStatus = null!;
    private DataGridView _gridFiles = null!;
    private Label _lblCount = null!;
    private Button _btnPick = null!;
    private Button _btnRestore = null!;
    private OrangeProgressBar _progress = null!;
    private Label _lblPercent = null!;
    private Label _lblRestoreStatus = null!;
    private bool _running;

    public RestoreForm()
    {
        Text = "ST2 · Restaurar base";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.UiFont();
        BackColor = UiTheme.AppBack;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        MinimumSize = new Size(640, 480);
        BuildUi();
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

        shell.Controls.Add(UiTheme.CreateSubFormHeader("ST2 · Restaurar base", (_, _) => Close()), 0, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(10, 8, 10, 10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136f));

        _lblConnStatus = UiTheme.CreateConnectionStatusLabel();
        root.Controls.Add(_lblConnStatus, 0, 0);

        var warn = UiTheme.CreateCompactAlertBanner(
            "Atención",
            "Si la base ya existe, la sobreescribira sobre la misma instancia;" + Environment.NewLine +
            "Durante éste proceso se desconectará a todos los usuarios activos.",
            UiTheme.Danger,
            UiTheme.IncludeNoBg,
            UiTheme.IncludeNoFg);
        root.Controls.Add(warn, 0, 1);

        const int maxVisibleFiles = 7;
        const int restoreGridRowHeight = 24;
        var listHeight = UiTheme.GridHeaderHeight + restoreGridRowHeight * maxVisibleFiles + 16;

        var filesInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 0)
        };
        filesInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
        filesInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        filesInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var pickHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(4, 6, 4, 0)
        };
        _btnPick = new Button
        {
            Text = "Elegir archivos .bak",
            Enabled = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Top
        };
        UiTheme.StylePickFileButton(_btnPick);
        _btnPick.Click += async (_, _) => await PickFilesAsync().ConfigureAwait(true);
        pickHost.Controls.Add(_btnPick);
        filesInner.Controls.Add(pickHost, 0, 0);

        _lblCount = new Label
        {
            Text = "Elegí los archivos .bak a restaurar.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.25f),
            Margin = new Padding(4, 0, 0, 0)
        };
        filesInner.Controls.Add(_lblCount, 0, 1);

        _gridFiles = CreateRestoreFilesGrid();
        var gridShell = UiTheme.WrapGridFill(_gridFiles);
        gridShell.MinimumSize = new Size(0, listHeight);
        filesInner.Controls.Add(gridShell, 0, 2);
        root.Controls.Add(CardSection.CreateFill("Bases a restaurar", filesInner, new Padding(0, 0, 0, 4)), 0, 2);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = UiTheme.AppBack,
            Margin = new Padding(0)
        };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 72f));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));

        var actionHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(0, 8, 0, 12)
        };
        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.AppBack
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _btnRestore = new RoundedActionButton { Text = "Restaurar Bases", Enabled = false };
        UiTheme.StyleRestoreButton(_btnRestore);
        _btnRestore.Margin = new Padding(0);
        _btnRestore.Click += async (_, _) => await RunRestoreAsync().ConfigureAwait(true);
        actionRow.Controls.Add(_btnRestore, 0, 0);
        _lblRestoreStatus = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Margin = new Padding(14, 10, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.5f)
        };
        actionRow.Controls.Add(_lblRestoreStatus, 1, 0);
        actionHost.Controls.Add(actionRow);
        bottom.Controls.Add(actionHost, 0, 0);

        var progShell = UiTheme.CreateProgressShell(out _progress, out _lblPercent);
        progShell.Dock = DockStyle.Fill;
        bottom.Controls.Add(progShell, 0, 1);
        root.Controls.Add(bottom, 0, 3);

        shell.Controls.Add(root, 0, 1);
        Controls.Add(shell);
        UiTheme.BindSubFormScreenSizing(this, 720, 600);
        Shown += (_, _) => ApplySessionState();
    }

    private void ApplySessionState()
    {
        _connectionString = AppSession.ConnectionString;
        UiTheme.SetConnectionStatusLabel(_lblConnStatus, AppSession.IsConnected, AppSession.ServerName);
        var enabled = AppSession.IsConnected;
        _btnPick.Enabled = enabled && !_running;
        _gridFiles.Enabled = enabled;
        _btnRestore.Enabled = enabled && !_running && _files.Count > 0;
        _lblCount.Text = enabled
            ? (_files.Count == 0 ? "Ningún archivo seleccionado." : $"{_files.Count} archivo(s) seleccionado(s).")
            : "Conectá al servidor SQL desde la pantalla principal.";
    }

    private void SetRestoreStatus(string text)
    {
        _lblRestoreStatus.Text = text;
        _lblRestoreStatus.ForeColor = UiTheme.TextMuted;
    }

    private void ApplyRestoreProgress(int percent, string status, Stopwatch sw)
    {
        var n = Math.Clamp(percent, 0, 100);
        _progress.Value = n;
        _lblPercent.Text = $"{n} %";
        SetRestoreStatus(UiTheme.FormatProgressStatus(status, n, sw));
    }

    private DataGridView CreateRestoreFilesGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            Enabled = false,
            Margin = new Padding(0),
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        UiTheme.StyleDataGrid(grid);
        grid.RowTemplate.Height = 24;
        grid.DefaultCellStyle.Font = UiTheme.UiFont(8.25f);
        grid.AlternatingRowsDefaultCellStyle.Font = UiTheme.UiFont(8.25f);
        grid.SortCompare += GridFiles_SortCompare;
        grid.Sorted += (_, _) =>
        {
            PinManagerRowFirst();
            RenumberGridRows();
        };
        grid.CellFormatting += GridFiles_CellFormatting;

        var colNum = new DataGridViewTextBoxColumn
        {
            Name = "colNum",
            HeaderText = "#",
            Width = 36,
            MinimumWidth = 36,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
        };
        grid.Columns.Add(colNum);

        var colNombre = new DataGridViewTextBoxColumn
        {
            Name = "colNombre",
            HeaderText = "Nombre",
            FillWeight = 34,
            MinimumWidth = 120,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        grid.Columns.Add(colNombre);

        var colTamano = new DataGridViewTextBoxColumn
        {
            Name = "colTamano",
            HeaderText = "Tamaño",
            FillWeight = 16,
            MinimumWidth = 72,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        grid.Columns.Add(colTamano);

        var colFecha = new DataGridViewTextBoxColumn
        {
            Name = "colFecha",
            HeaderText = "Fecha del backup",
            FillWeight = 22,
            MinimumWidth = 110,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        grid.Columns.Add(colFecha);

        var colAccion = new DataGridViewTextBoxColumn
        {
            Name = "colAccion",
            HeaderText = "Acción",
            FillWeight = 18,
            MinimumWidth = 90,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        colAccion.DefaultCellStyle.ForeColor = UiTheme.Danger;
        grid.Columns.Add(colAccion);

        return grid;
    }

    private string ManagerDbName() =>
        string.IsNullOrWhiteSpace(_config.ManagerDatabaseName) ? "manager" : _config.ManagerDatabaseName.Trim();

    private bool IsManagerPreview(RestoreCoordinator.RestoreFilePreview p) =>
        p.DatabaseName?.Equals(ManagerDbName(), StringComparison.OrdinalIgnoreCase) == true;

    private void GridFiles_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;

        var rowA = grid.Rows[e.RowIndex1];
        var rowB = grid.Rows[e.RowIndex2];
        if (rowA.Tag is not RestoreCoordinator.RestoreFilePreview a ||
            rowB.Tag is not RestoreCoordinator.RestoreFilePreview b)
        {
            e.SortResult = 0;
            return;
        }

        var colName = grid.Columns[e.Column.Index].Name;
        e.SortResult = colName switch
        {
            "colNombre" => string.Compare(a.DatabaseName, b.DatabaseName, StringComparison.CurrentCultureIgnoreCase),
            "colTamano" => CompareNullableLong(GetFileLength(a.LocalPath), GetFileLength(b.LocalPath)),
            "colFecha" => Nullable.Compare(a.BackupDate, b.BackupDate),
            "colAccion" => string.Compare(
                a.DatabaseExists == true ? "sobrescribe" : "",
                b.DatabaseExists == true ? "sobrescribe" : "",
                StringComparison.CurrentCultureIgnoreCase),
            _ => 0
        };

        if (e.SortResult == 0)
            e.SortResult = string.Compare(a.DatabaseName, b.DatabaseName, StringComparison.CurrentCultureIgnoreCase);
    }

    private static int CompareNullableLong(long? a, long? b)
    {
        if (a.HasValue && b.HasValue)
            return a.Value.CompareTo(b.Value);
        if (a.HasValue)
            return -1;
        if (b.HasValue)
            return 1;
        return 0;
    }

    private static long? GetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    private void GridFiles_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.CellStyle is null)
            return;
        if (grid.Rows[e.RowIndex].Tag is not RestoreCoordinator.RestoreFilePreview)
            return;

        if (grid.Columns[e.ColumnIndex].Name == "colNombre")
            e.CellStyle.Font = UiTheme.UiFont(8.25f, FontStyle.Bold);
    }

    private void PinManagerRowFirst()
    {
        if (_pinningManager)
            return;

        var mgr = ManagerDbName();
        var mgrIndex = -1;
        var firstIndex = -1;

        for (var i = 0; i < _gridFiles.Rows.Count; i++)
        {
            var gr = _gridFiles.Rows[i];
            if (gr.IsNewRow)
                continue;

            if (firstIndex < 0)
                firstIndex = i;

            if (gr.Tag is RestoreCoordinator.RestoreFilePreview p &&
                p.DatabaseName?.Equals(mgr, StringComparison.OrdinalIgnoreCase) == true)
                mgrIndex = i;
        }

        if (mgrIndex < 0 || firstIndex < 0 || mgrIndex == firstIndex)
            return;

        _pinningManager = true;
        try
        {
            var row = _gridFiles.Rows[mgrIndex];
            _gridFiles.Rows.RemoveAt(mgrIndex);
            if (mgrIndex < firstIndex)
                firstIndex--;

            _gridFiles.Rows.Insert(firstIndex, row);
        }
        finally
        {
            _pinningManager = false;
        }
    }

    private void RenumberGridRows()
    {
        var n = 1;
        foreach (DataGridViewRow gr in _gridFiles.Rows)
        {
            if (gr.IsNewRow)
                continue;
            gr.Cells["colNum"].Value = n++;
        }
    }

    private void PopulateFilesGrid()
    {
        _gridFiles.Rows.Clear();
        var ordered = _filePreviews
            .OrderByDescending(IsManagerPreview)
            .ThenBy(p => p.DatabaseName, StringComparer.CurrentCultureIgnoreCase);

        foreach (var p in ordered)
        {
            var i = _gridFiles.Rows.Add(
                "",
                p.DatabaseName ?? "",
                TryFormatSize(p.LocalPath) ?? "",
                p.BackupDate?.ToString("dd/MM/yyyy HH:mm") ?? "",
                p.DatabaseExists == true ? "sobrescribe" : "");
            _gridFiles.Rows[i].Tag = p;
        }

        RenumberGridRows();
    }

    private static void PaintRoundedBorder(Graphics g, Rectangle rect)
    {
        if (rect.Width <= 4 || rect.Height <= 4)
            return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, rect.Width - 1, rect.Height - 1);
        using var path = UiTheme.RoundedRectangle(r, 10);
        using var pen = new Pen(UiTheme.Border, 1f);
        g.DrawPath(pen, path);
    }

    private async Task PickFilesAsync()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            MessageBox.Show(this, "Conectá al servidor SQL desde la pantalla principal.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dlg = new OpenFileDialog
        {
            Title = "Seleccioná los archivos .bak a restaurar",
            Filter = "Backups SQL Server (*.bak)|*.bak|Todos los archivos (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        _files.Clear();
        _files.AddRange(dlg.FileNames);
        await RefreshFilePreviewsAsync().ConfigureAwait(true);
    }

    private async Task RefreshFilePreviewsAsync()
    {
        _filePreviews.Clear();
        _gridFiles.Rows.Clear();
        _btnRestore.Enabled = false;

        if (_files.Count == 0)
        {
            _lblCount.Text = "Elegí los archivos .bak a restaurar.";
            return;
        }

        _lblCount.Text = $"Analizando {_files.Count} archivo(s)…";
        SetRestoreStatus("Leyendo metadatos del backup…");

        foreach (var path in _files)
        {
            RestoreCoordinator.RestoreFilePreview preview;
            if (!string.IsNullOrEmpty(_connectionString))
            {
                preview = await _restore.PeekBackupAsync(_connectionString, path, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            else
            {
                preview = new RestoreCoordinator.RestoreFilePreview { LocalPath = path };
                preview.DatabaseName = Path.GetFileNameWithoutExtension(path);
            }

            _filePreviews.Add(preview);
        }

        PopulateFilesGrid();
        PinManagerRowFirst();
        RenumberGridRows();

        _lblCount.Text = _files.Count == 1
            ? "1 archivo seleccionado."
            : $"{_files.Count} archivos seleccionados.";
        _btnRestore.Enabled = !_running && _files.Count > 0;
        SetRestoreStatus("");
    }

    private async Task RunRestoreAsync()
    {
        if (_running || _files.Count == 0 || string.IsNullOrEmpty(_connectionString))
            return;

        SetRunning(true);
        _progress.Value = 0;
        _lblPercent.Text = "0 %";
        SetRestoreStatus("Iniciando…");
        var log = new Progress<string>(line => Debug.WriteLine("[SBRestore] " + line));
        var sw = Stopwatch.StartNew();

        var plan = new List<RestoreCoordinator.RestorePlanItem>();
        try
        {
            SetRestoreStatus("Analizando archivos…");
            var staging = await SqlBackupStagingResolver.ResolveSqlWritableCandidatesAsync(
                    _connectionString, log, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var file in _files)
            {
                plan.Add(await _restore.AnalyzeAsync(_connectionString, file, staging, log, CancellationToken.None)
                    .ConfigureAwait(true));
            }

            var resumen = string.Join("\n", plan.Select(p =>
                $"   • {p.DatabaseName}  ←  {Path.GetFileName(p.LocalBakPath)}" +
                (p.DatabaseExists ? "   [SE SOBRESCRIBE]" : "   [se crea nueva]")));

            if (MessageBox.Show(
                    this,
                    "Se van a restaurar las siguientes bases (WITH REPLACE):\n\n" + resumen +
                    "\n\n¿Confirmás la restauración?",
                    "Confirmar restauración",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                AppendLog("Restauración cancelada por el usuario.");
                SetRestoreStatus("Cancelado.");
                CleanupStaging(plan);
                return;
            }

            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                var basePct = i * 100 / plan.Count;
                var slice = Math.Max(1, 100 / plan.Count);
                var statusPrefix = $"{i + 1}/{plan.Count} · «{item.DatabaseName}»";
                SetRestoreStatus($"{statusPrefix} — iniciando…");

                var fileProgress = new Progress<RestoreCoordinator.RestoreProgress>(u =>
                {
                    var overall = Math.Clamp(basePct + (u.Percent * slice / 100), 0, 99);
                    var shortStatus = $"{statusPrefix} — {u.Percent} %";
                    ApplyRestoreProgress(overall, shortStatus, sw);
                });

                ApplyRestoreProgress(Math.Clamp(basePct + (slice * 95 / 100), 0, 99),
                    $"{statusPrefix} — restaurando…", sw);

                await _restore.ExecuteAsync(_connectionString, item, log, fileProgress, CancellationToken.None)
                    .ConfigureAwait(true);

                ApplyRestoreProgress(Math.Clamp(basePct + slice - 1, 0, 99),
                    $"{statusPrefix} — bas_server y claves ADMIN/CNV", sw);
            }

            _progress.Value = 100;
            _lblPercent.Text = "100 %";
            SetRestoreStatus($"Listo — se restauraron {plan.Count} base(s), bas_server y claves ADMIN/CNV. · ⏱ " +
                             UiTheme.FormatDuration(sw.Elapsed));
            MessageBox.Show(this,
                plan.Count == 1
                    ? $"La base «{plan[0].DatabaseName}» se restauró correctamente.\r\n\r\n" +
                      "Se actualizó bas_server y se blanquearon las claves de ADMIN y CNV."
                    : $"Se restauraron {plan.Count} bases correctamente.\r\n\r\n" +
                      "En cada una se actualizó bas_server y se blanquearon las claves de ADMIN y CNV.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SBRestore] ERROR: " + ex);
            SetRestoreStatus("La restauración falló.");
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError(
                    "No se pudo completar la restauración. Verificá el .bak, permisos del servicio SQL y que la base no esté en uso.",
                    ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CleanupStaging(plan);
            SetRunning(false);
        }
    }

    private void CleanupStaging(IEnumerable<RestoreCoordinator.RestorePlanItem> plan)
    {
        foreach (var item in plan)
            RestoreCoordinator.TryDelete(item.ClientCleanupPath);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _btnRestore.Enabled = !running && _files.Count > 0 && !string.IsNullOrEmpty(_connectionString);
        _btnPick.Enabled = !running && !string.IsNullOrEmpty(_connectionString);
        if (running)
        {
            _progress.Value = 0;
            _lblPercent.Text = "0 %";
            _lblRestoreStatus.Text = "";
        }
    }

    private static void AppendLog(string line) => Debug.WriteLine("[SBRestore] " + line);

    private static string? TryFormatSize(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            var u = 0;
            while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
            return $"{size:0.#} {units[u]}";
        }
        catch { return null; }
    }
}
