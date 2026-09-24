using System.Diagnostics;
using System.Drawing.Drawing2D;
using SBBackup.Models;
using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

public partial class Form1 : Form
{
    private readonly AppConfig _config;
    private readonly BejermanDatabaseCatalogBuilder _bejermanCatalog;
    private readonly TransactionLogService _logService;
    private readonly BackupCoordinator _backup;

    private string? _connectionString;
    private readonly List<ServerDatabaseRow> _rows = [];

    private TextBox _txtFolder = null!;
    private string _outputFolder = "";
    private Button _btnRun = null!;
    private Button _btnSchedule = null!;
    private DataGridView _grid = null!;
    private OrangeProgressBar _progress = null!;
    private Label _lblProgressPct = null!;
    private Label _lblBackupStatus = null!;
    private Label _lblConnStatus = null!;
    private WheelSafeComboBox _cmbEmpresa = null!;
    private bool _suspendEmpresaFilter;
    private bool _pinningManager;
    private TableLayoutPanel? _rootLayout;
    private Panel? _gridShell;
    private CancellationTokenSource? _lifetimeCts;

    private sealed record EmpresaOption(string? Code, string Label)
    {
        public override string ToString() => Label;
    }

    public Form1()
    {
        _config = AppConfig.Load(AppContext.BaseDirectory);
        _bejermanCatalog = new BejermanDatabaseCatalogBuilder(_config);
        _logService = new TransactionLogService();
        _backup = new BackupCoordinator(_logService);

        InitializeComponent();
        UiTheme.ApplyDpiAwareScaling(this);
        Ui.AppIcon.Apply(this);
        Text = "ST2 · Backup de bases";
        MinimumSize = new Size(880, 420);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        Font = UiTheme.UiFont();
        BackColor = UiTheme.AppBack;
        _lifetimeCts = new CancellationTokenSource();
        FormClosing += (_, _) =>
        {
            try { _lifetimeCts?.Cancel(); } catch { /* ignore */ }
        };
        FormClosed += (_, _) =>
        {
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        };
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
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.SubFormHeaderHeight));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var header = UiTheme.CreateSubFormHeader("ST2 · Backup de bases", (_, _) => Close());
        shell.Controls.Add(header, 0, 0);

        _rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(10, 6, 10, 12)
        };
        var root = _rootLayout;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.CardSectionFixedHeight(40)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136f));

        var topBar = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        topBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        topBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));

        _lblConnStatus = UiTheme.CreateConnectionStatusLabel();
        topBar.Controls.Add(_lblConnStatus, 0, 0);

        var empresaPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.EmpresaPanelBg,
            Padding = new Padding(10, 8, 10, 8),
            Margin = Padding.Empty
        };
        empresaPanel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, empresaPanel.Width - 1, empresaPanel.Height - 1);
            if (r.Width <= 4)
                return;
            using var path = UiTheme.RoundedRectangle(r, 8);
            using var fill = new SolidBrush(UiTheme.EmpresaPanelBg);
            e.Graphics.FillPath(fill, path);
            using var pen = new Pen(UiTheme.Border, 1f);
            e.Graphics.DrawPath(pen, path);
        };
        var empresaLay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        empresaLay.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
        empresaLay.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        empresaLay.Controls.Add(new Label
        {
            Text = "Seleccioná la empresa",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.25f, FontStyle.Bold),
            Padding = new Padding(2, 0, 0, 2)
        }, 0, 0);

        _cmbEmpresa = new WheelSafeComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled = false
        };
        UiTheme.StyleEmpresaCombo(_cmbEmpresa);
        _cmbEmpresa.RedirectWheel = ScrollGridByWheel;
        _cmbEmpresa.Items.Add(new EmpresaOption(null, "Todas"));
        _cmbEmpresa.SelectedIndex = 0;
        _cmbEmpresa.SelectedIndexChanged += (_, _) => ApplyRowVisibilityFilter();
        empresaLay.Controls.Add(_cmbEmpresa, 0, 1);
        empresaPanel.Controls.Add(empresaLay);
        topBar.Controls.Add(empresaPanel, 0, 1);
        root.Controls.Add(CardSection.CreateInlineBarFill(topBar), 0, 0);

        _grid = CreateDatabaseGrid();
        _gridShell = UiTheme.WrapGridFill(_grid);
        _grid.MouseEnter += (_, _) => _grid.Focus();
        _gridShell.MouseEnter += (_, _) => _grid.Focus();
        var listOuter = CardSection.CreateFill("Selecciona las bases a respaldar:", _gridShell);
        root.Controls.Add(listOuter, 0, 1);

        _outputFolder = OutputPathHelper.GetDefaultOutputDirectory();
        var folderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.EmpresaPanelBg,
            Padding = new Padding(12, 6, 12, 6),
            Margin = Padding.Empty
        };
        _txtFolder = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Text = _outputFolder,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9.5f),
            BackColor = UiTheme.EmpresaPanelBg,
            Cursor = Cursors.IBeam,
            ShortcutsEnabled = true
        };
        _txtFolder.GotFocus += (_, _) => _txtFolder.SelectAll();
        folderPanel.Controls.Add(_txtFolder);
        root.Controls.Add(CardSection.CreateFixed("Carpeta destino de los Backups", folderPanel, 40), 0, 2);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = UiTheme.AppBack,
            Margin = new Padding(0)
        };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 78f));
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
            ColumnCount = 3,
            RowCount = 1,
            BackColor = UiTheme.AppBack
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _btnRun = new RoundedActionButton { Text = "Realizar backup" };
        UiTheme.StyleBackupButton(_btnRun);
        _btnRun.Margin = new Padding(0, 4, 0, 4);
        _btnRun.Click += async (_, _) => await RunAsync().ConfigureAwait(true);
        actionRow.Controls.Add(_btnRun, 0, 0);

        _btnSchedule = new RoundedActionButton { Text = "Programar backups automáticos" };
        UiTheme.StyleScheduleButton(_btnSchedule);
        _btnSchedule.Click += (_, _) => OpenScheduleDialog();
        actionRow.Controls.Add(_btnSchedule, 1, 0);

        _lblBackupStatus = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Margin = new Padding(14, 10, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.5f)
        };
        actionRow.Controls.Add(_lblBackupStatus, 2, 0);
        actionHost.Controls.Add(actionRow);
        bottom.Controls.Add(actionHost, 0, 0);

        var progShell = UiTheme.CreateProgressShell(out _progress, out _lblProgressPct);
        progShell.Dock = DockStyle.Fill;
        bottom.Controls.Add(progShell, 0, 1);
        root.Controls.Add(bottom, 0, 3);

        shell.Controls.Add(root, 0, 1);
        Controls.Add(shell);
        UiTheme.BindSubFormScreenSizing(this, 960, 680);
        Shown += async (_, _) => await LoadDatabasesAsync().ConfigureAwait(true);
    }

    private void ScrollGridByWheel(MouseEventArgs e)
    {
        if (_grid.Rows.Count == 0)
            return;

        var lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
        var step = e.Delta > 0 ? -lines : lines;
        var idx = _grid.FirstDisplayedScrollingRowIndex;
        if (idx < 0)
            idx = 0;

        var next = Math.Clamp(idx + step, 0, _grid.Rows.Count - 1);
        try
        {
            _grid.FirstDisplayedScrollingRowIndex = next;
        }
        catch (ArgumentOutOfRangeException)
        {
            // filas ocultas / sin scroll
        }
    }

    private DataGridView CreateDatabaseGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            MultiSelect = true,
            Margin = new Padding(0)
        };
        UiTheme.StyleDataGrid(grid);
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.RowTemplate.Height = UiTheme.GridRowHeight;
        grid.RowTemplate.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.CellClick += Grid_CellClick_Incluir;
        grid.CellDoubleClick += Grid_CellDoubleClick_Incluir;
        grid.CellMouseDown += Grid_CellMouseDown_Context;
        grid.CellFormatting += Grid_CellFormatting;
        grid.CellPainting += Grid_CellPainting_Incluir;
        grid.Paint += Grid_Paint_EmptyState;
        grid.SortCompare += Grid_SortCompare;
        grid.Sorted += (_, _) => PinManagerRowFirst();
        grid.ContextMenuStrip = CreateGridContextMenu(grid);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;

        var colSel = new DataGridViewTextBoxColumn
        {
            Name = "colSel",
            HeaderText = "Incluir",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 72,
            MinimumWidth = 72,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        colSel.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        colSel.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        colSel.DefaultCellStyle.Font = UiTheme.UiFont(9.75f, FontStyle.Bold);
        colSel.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        colSel.HeaderCell.Style.Font = UiTheme.UiFont(9.5f, FontStyle.Bold);
        colSel.HeaderCell.Style.ForeColor = UiTheme.TextPrimary;
        grid.Columns.Add(colSel);

        var colTipo = new DataGridViewTextBoxColumn
        {
            Name = "colTipo",
            HeaderText = "Tipo",
            ReadOnly = true,
            FillWeight = 24,
            MinimumWidth = 130,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        colTipo.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        colTipo.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.Columns.Add(colTipo);

        var colNombre = new DataGridViewTextBoxColumn
        {
            Name = "colNombre",
            HeaderText = "Nombre",
            ReadOnly = true,
            FillWeight = 46,
            MinimumWidth = 200,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        colNombre.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        colNombre.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.Columns.Add(colNombre);

        var colDb = new DataGridViewTextBoxColumn
        {
            Name = "colDb",
            HeaderText = "Base en SQL Server",
            ReadOnly = true,
            FillWeight = 30,
            MinimumWidth = 140,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        colDb.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        colDb.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.Columns.Add(colDb);

        return grid;
    }

    private void AppendLog(string line) =>
        System.Diagnostics.Debug.WriteLine("[SBBackup] " + line);

    private ContextMenuStrip CreateGridContextMenu(DataGridView grid)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var incluir = new ToolStripMenuItem("Incluir en el backup");
        incluir.Click += (_, _) => SetTargetRowsIncluded(grid, true);
        var excluir = new ToolStripMenuItem("Quitar del backup");
        excluir.Click += (_, _) => SetTargetRowsIncluded(grid, false);
        var alternar = new ToolStripMenuItem("Alternar incluir / quitar");
        alternar.Click += (_, _) => ToggleTargetRowsIncluded(grid);
        var incluirVisibles = new ToolStripMenuItem("Incluir todas las visibles");
        incluirVisibles.Click += (_, _) => SetAllVisibleRowsIncluded(grid, true);
        var excluirVisibles = new ToolStripMenuItem("Quitar todas las visibles");
        excluirVisibles.Click += (_, _) => SetAllVisibleRowsIncluded(grid, false);

        menu.Items.Add(incluir);
        menu.Items.Add(excluir);
        menu.Items.Add(alternar);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(incluirVisibles);
        menu.Items.Add(excluirVisibles);

        menu.Opening += (_, _) =>
        {
            var targets = GetContextTargetRows(grid).ToList();
            var hasTargets = targets.Count > 0;
            incluir.Enabled = hasTargets;
            excluir.Enabled = hasTargets;
            alternar.Enabled = hasTargets;
            incluirVisibles.Enabled = grid.Rows.Cast<DataGridViewRow>().Any(r => r.Visible && !r.IsNewRow);
            excluirVisibles.Enabled = incluirVisibles.Enabled;
        };

        return menu;
    }

    private void Grid_CellMouseDown_Context(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (sender is not DataGridView grid || e.Button != MouseButtons.Right || e.RowIndex < 0)
            return;
        var gr = grid.Rows[e.RowIndex];
        if (gr.IsNewRow || !gr.Visible)
            return;
        grid.ClearSelection();
        gr.Selected = true;
    }

    private void Grid_CellClick_Incluir(object? sender, DataGridViewCellEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
            return;
        if (grid.Columns[e.ColumnIndex].Name != "colSel")
            return;
        ToggleRowInclusion(grid.Rows[e.RowIndex]);
    }

    private void Grid_CellDoubleClick_Incluir(object? sender, DataGridViewCellEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0)
            return;
        if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "colSel")
            return;
        var gr = grid.Rows[e.RowIndex];
        if (gr.IsNewRow || !gr.Visible)
            return;
        ToggleRowInclusion(gr);
    }

    private static void SetRowInclusion(DataGridViewRow gr, bool selected)
    {
        if (gr.IsNewRow || gr.Tag is not ServerDatabaseRow row)
            return;
        row.Selected = selected;
        gr.Cells["colSel"].Value = selected ? "Sí" : "No";
        gr.DataGridView?.InvalidateCell(gr.Cells["colSel"]);
    }

    private static void ToggleRowInclusion(DataGridViewRow gr)
    {
        if (gr.IsNewRow || gr.Tag is not ServerDatabaseRow row)
            return;
        SetRowInclusion(gr, !row.Selected);
    }

    private static IEnumerable<DataGridViewRow> GetContextTargetRows(DataGridView grid)
    {
        var selected = grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Visible && r.Tag is ServerDatabaseRow)
            .ToList();
        if (selected.Count > 0)
            return selected;

        if (grid.CurrentRow is { IsNewRow: false, Visible: true, Tag: ServerDatabaseRow })
            return [grid.CurrentRow];

        return [];
    }

    private static void SetTargetRowsIncluded(DataGridView grid, bool included)
    {
        foreach (var gr in GetContextTargetRows(grid))
            SetRowInclusion(gr, included);
    }

    private static void ToggleTargetRowsIncluded(DataGridView grid)
    {
        foreach (var gr in GetContextTargetRows(grid))
            ToggleRowInclusion(gr);
    }

    private static void SetAllVisibleRowsIncluded(DataGridView grid, bool included)
    {
        foreach (DataGridViewRow gr in grid.Rows)
        {
            if (gr.IsNewRow || !gr.Visible)
                continue;
            SetRowInclusion(gr, included);
        }
    }

    private void Grid_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;

        var rowA = grid.Rows[e.RowIndex1];
        var rowB = grid.Rows[e.RowIndex2];
        if (rowA.Tag is not ServerDatabaseRow a || rowB.Tag is not ServerDatabaseRow b)
        {
            e.SortResult = 0;
            return;
        }

        var colName = grid.Columns[e.Column.Index].Name;
        e.SortResult = colName switch
        {
            "colSel" => a.Selected.CompareTo(b.Selected),
            "colNombre" => CompareNombreSort(a, b),
            "colTipo" => string.Compare(a.GridTipo, b.GridTipo, StringComparison.CurrentCultureIgnoreCase),
            "colDb" => string.Compare(a.PhysicalName, b.PhysicalName, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(
                Convert.ToString(e.CellValue1) ?? "",
                Convert.ToString(e.CellValue2) ?? "",
                StringComparison.CurrentCultureIgnoreCase)
        };

        if (e.SortResult == 0)
            e.SortResult = string.Compare(a.PhysicalName, b.PhysicalName, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareNombreSort(ServerDatabaseRow a, ServerDatabaseRow b)
    {
        var na = a.ExerciseSortOrder ?? ExerciseDisplayHelper.ResolveSortNumber(a.GridNombre, a.PhysicalName);
        var nb = b.ExerciseSortOrder ?? ExerciseDisplayHelper.ResolveSortNumber(b.GridNombre, b.PhysicalName);
        if (na.HasValue && nb.HasValue)
        {
            var c = na.Value.CompareTo(nb.Value);
            if (c != 0)
                return c;
        }
        else if (na.HasValue)
            return -1;
        else if (nb.HasValue)
            return 1;

        return string.Compare(a.GridNombre, b.GridNombre, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
            return;
        if (e.CellStyle is null || e.ColumnIndex >= grid.Columns.Count)
            return;
        if (grid.Rows[e.RowIndex].Tag is not ServerDatabaseRow r)
            return;

        var colName = grid.Columns[e.ColumnIndex].Name;
        if (colName == "colSel")
        {
            e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            e.CellStyle.Padding = Padding.Empty;
            return;
        }

        if (colName == "colDb")
            e.CellStyle.Font = UiTheme.UiFont(9.5f, FontStyle.Bold);
    }

    private void PinManagerRowFirst()
    {
        if (_pinningManager)
            return;

        var mgr = ManagerDbName();
        var mgrIndex = -1;
        var firstVisibleIndex = -1;

        for (var i = 0; i < _grid.Rows.Count; i++)
        {
            var gr = _grid.Rows[i];
            if (gr.IsNewRow || !gr.Visible)
                continue;

            if (firstVisibleIndex < 0)
                firstVisibleIndex = i;

            if (gr.Tag is ServerDatabaseRow r && r.PhysicalName.Equals(mgr, StringComparison.OrdinalIgnoreCase))
                mgrIndex = i;
        }

        if (mgrIndex < 0 || firstVisibleIndex < 0 || mgrIndex == firstVisibleIndex)
            return;

        _pinningManager = true;
        try
        {
            var row = _grid.Rows[mgrIndex];
            _grid.Rows.RemoveAt(mgrIndex);
            if (mgrIndex < firstVisibleIndex)
                firstVisibleIndex--;

            _grid.Rows.Insert(firstVisibleIndex, row);
        }
        finally
        {
            _pinningManager = false;
        }
    }

    private void Grid_CellPainting_Incluir(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DataGridView grid)
            return;
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.ColumnIndex >= grid.Columns.Count)
            return;
        if (grid.Columns[e.ColumnIndex].Name != "colSel")
            return;
        if (grid.Rows[e.RowIndex].Tag is not ServerDatabaseRow r)
            return;

        e.PaintBackground(e.CellBounds, true);

        var text = r.Selected ? "Sí" : "No";
        var bg = r.Selected ? UiTheme.IncludeYesBg : UiTheme.IncludeNoBg;
        var fg = r.Selected ? UiTheme.IncludeYesFg : UiTheme.IncludeNoFg;

        var g = e.Graphics!;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int pillW = 40;
        const int pillH = 22;
        var px = e.CellBounds.X + (e.CellBounds.Width - pillW) / 2;
        var py = e.CellBounds.Y + (e.CellBounds.Height - pillH) / 2;
        var pill = new Rectangle(px, py, pillW, pillH);

        using (var path = UiTheme.RoundedRectangle(pill, pillH / 2))
        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, path);

        TextRenderer.DrawText(
            g, text, UiTheme.UiFont(9.5f, FontStyle.Bold), pill, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        e.Handled = true;
    }

    private void Grid_Paint_EmptyState(object? sender, PaintEventArgs e)
    {
        if (sender is not DataGridView grid || grid.Rows.Count > 0)
            return;

        var msg = string.IsNullOrEmpty(_connectionString)
            ? "Conectá para ver las bases"
            : "No se encontraron bases para mostrar";

        TextRenderer.DrawText(
            e.Graphics, msg, UiTheme.UiFont(11.5f), grid.ClientRectangle, UiTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void PopulateEmpresaCombo()
    {
        _suspendEmpresaFilter = true;
        try
        {
            _cmbEmpresa.Items.Clear();
            _cmbEmpresa.Items.Add(new EmpresaOption(null, "Todas"));
            var groups = _rows
                .Where(r => !string.IsNullOrEmpty(r.MatchedEmpCode))
                .GroupBy(r => r.MatchedEmpCode!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().FriendlyName, StringComparer.CurrentCultureIgnoreCase);

            foreach (var g in groups)
            {
                var raz = g.First().FriendlyName;
                if (string.IsNullOrWhiteSpace(raz))
                    raz = g.Key;
                _cmbEmpresa.Items.Add(new EmpresaOption(g.Key, $"({g.Key}) — {raz}"));
            }

            _cmbEmpresa.SelectedIndex = 0;
        }
        finally
        {
            _suspendEmpresaFilter = false;
        }

        var maxText = 380;
        if (_cmbEmpresa.Items.Count > 0)
        {
            using var g = _cmbEmpresa.CreateGraphics();
            foreach (var item in _cmbEmpresa.Items)
            {
                var text = item?.ToString() ?? "";
                var w = (int)Math.Ceiling(g.MeasureString(text, _cmbEmpresa.Font).Width) + 56;
                if (w > maxText)
                    maxText = w;
            }
        }

        var comboWidth = Math.Max(380, Math.Min(maxText, 560));
        _cmbEmpresa.DropDownWidth = Math.Max(480, comboWidth);

        ApplyRowVisibilityFilter();
    }

    private static void AddTopBarLabel(TableLayoutPanel bar, string text, int column)
    {
        var lbl = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            Margin = new Padding(0, 0, 4, 0)
        };
        UiTheme.StyleLabel(lbl, muted: true);
        bar.Controls.Add(lbl, column, 0);
    }

    private void ApplyRowVisibilityFilter()
    {
        if (_cmbEmpresa.Items.Count == 0)
            return;

        var opt = _suspendEmpresaFilter ? null : _cmbEmpresa.SelectedItem as EmpresaOption;
        var code = opt?.Code;
        var mgr = ManagerDbName();

        foreach (DataGridViewRow gr in _grid.Rows)
        {
            if (gr.IsNewRow || gr.Tag is not ServerDatabaseRow r)
                continue;

            var isMgr = r.PhysicalName.Equals(mgr, StringComparison.OrdinalIgnoreCase);
            var matchesEmpresa = code is null
                || isMgr
                || (r.MatchedEmpCode is not null && r.MatchedEmpCode.Equals(code, StringComparison.OrdinalIgnoreCase));

            gr.Visible = matchesEmpresa;
        }

        PinManagerRowFirst();
    }

    private async Task LoadDatabasesAsync()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (!AppSession.IsConnected || string.IsNullOrEmpty(AppSession.ConnectionString))
        {
            SetConnectionStatus(false, null);
            return;
        }

        _connectionString = AppSession.ConnectionString;
        SetConnectionStatus(true, AppSession.ServerName);
        var ct = _lifetimeCts?.Token ?? CancellationToken.None;

        try
        {
            _suspendEmpresaFilter = true;
            _cmbEmpresa.Items.Clear();
            _cmbEmpresa.Items.Add(new EmpresaOption(null, "Todas"));
            _cmbEmpresa.SelectedIndex = 0;
            _cmbEmpresa.Enabled = false;
            _grid.Rows.Clear();
            _rows.Clear();

            var server = AppSession.ServerName ?? "";
            var progress = new Progress<string>(msg =>
            {
                if (!IsDisposed && IsHandleCreated)
                    AppendLog(msg);
            });
            AppendLog("Listando bases Bejerman (sin master, model, msdb ni tempdb)…");

            var list = await _bejermanCatalog
                .LoadAsync(_connectionString, progress, ct)
                .ConfigureAwait(true);

            if (IsDisposed || !IsHandleCreated || ct.IsCancellationRequested)
                return;

            _rows.AddRange(list);

            var mgrName = ManagerDbName();

            foreach (var row in _rows)
            {
                var isManager = row.PhysicalName.Equals(mgrName, StringComparison.OrdinalIgnoreCase);
                row.Selected = isManager;

                var incluir = row.Selected ? "Sí" : "No";
                var i = _grid.Rows.Add(incluir, row.GridTipo, row.GridNombre, row.PhysicalName);
                _grid.Rows[i].Tag = row;
            }

            PinManagerRowFirst();
            PopulateEmpresaCombo();
            _cmbEmpresa.Enabled = true;

            SetConnectionStatus(true, server);
            AppendLog($"Se listaron {_rows.Count} bases. Listo para respaldar.");
        }
        catch (OperationCanceledException)
        {
            // Ventana cerrada mientras cargaba: no mostrar error.
        }
        catch (Exception ex)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            SetConnectionStatus(false, null);
            try
            {
                MessageBox.Show(
                    this,
                    UserMessageSpanish.FriendlyError(
                        "No se pudo armar el listado de bases. Revisá la conexión y volvé a conectar desde la pantalla principal.",
                        ex),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (ObjectDisposedException)
            {
                // Form cerrado entre el check y el Show.
            }

            if (!IsDisposed && IsHandleCreated)
                AppendLog("ERROR: " + ex);
        }
        finally
        {
            if (!IsDisposed)
                _suspendEmpresaFilter = false;
        }
    }

    private void SetConnectionStatus(bool connected, string? server)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        UiTheme.SetConnectionStatusLabel(_lblConnStatus, connected, server);
    }

    private string ManagerDbName() => BejermanDatabaseRules.ResolveManagerDbName(_config);

    private List<ServerDatabaseRow> GetSelectedRows()
    {
        var sel = new List<ServerDatabaseRow>();
        foreach (DataGridViewRow gr in _grid.Rows)
        {
            if (gr.IsNewRow || !gr.Visible)
                continue;
            if (gr.Tag is ServerDatabaseRow { Selected: true } r)
                sel.Add(r);
        }

        return sel;
    }

    private void OpenScheduleDialog()
    {
        if (string.IsNullOrEmpty(_connectionString) || string.IsNullOrWhiteSpace(AppSession.ServerName))
        {
            MessageBox.Show(this, "Conectá al servidor SQL desde la pantalla principal.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "Todavía no se cargaron las bases. Esperá a que termine el listado.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var preselected = GetSelectedRows().Select(r => r.PhysicalName);
        using var dlg = new ScheduleBackupForm(AppSession.ServerName, _rows, preselected);
        dlg.ShowDialog(this);
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            MessageBox.Show(this, "Conectá al servidor SQL desde la pantalla principal.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var folder = string.IsNullOrWhiteSpace(_outputFolder)
            ? OutputPathHelper.GetDefaultOutputDirectory()
            : _outputFolder.Trim();
        if (!OutputPathHelper.TryValidateWriteAccess(folder, out var pathError))
        {
            MessageBox.Show(this, pathError, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        folder = OutputPathHelper.ResolveWorkspaceDirectory(folder);
        _outputFolder = folder;
        _txtFolder.Text = folder;

        var selected = GetSelectedRows();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Marcá al menos una base con «Sí» en la columna Incluir.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnRun.Enabled = false;
        _progress.Value = 0;
        _lblProgressPct.Text = "0 %";
        _lblBackupStatus.Text = "Iniciando…";

        var log = new Progress<string>(AppendLog);
        var sw = Stopwatch.StartNew();
        var uiProgress = new Progress<BackupProgressUpdate>(u => ApplyBackupProgress(u, sw));

        try
        {
            var result = await _backup.RunBackupsAsync(
                    _connectionString,
                    selected,
                    folder,
                    log,
                    uiProgress,
                    CancellationToken.None)
                .ConfigureAwait(true);
            AppendLog("Operación finalizada.");

            _progress.Value = 100;
            _lblProgressPct.Text = "100 %";

            if (!string.IsNullOrEmpty(result.ZipFilePath))
                OpenFolderInExplorer(result.OutputDirectory);
            else if (!string.IsNullOrEmpty(result.OutputDirectory))
                OpenFolderInExplorer(result.OutputDirectory);

            ResetUiAfterBackup();
        }
        catch (ZipCreationException ex)
        {
            AppendLog("ERROR ZIP: " + ex);
            var failure = ex.InnerException ?? ex;
            var prompt = UserMessageSpanish.ZipFailurePrompt(ex.ZipPath, ex.OutputDirectory, failure);
            var keepBak = MessageBox.Show(
                this,
                prompt,
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) == DialogResult.Yes;

            if (keepBak)
            {
                WorkspaceFileCleanup.RemoveZipFile(ex.ZipPath, log);
                OpenFolderInExplorer(ex.OutputDirectory);
                ResetUiAfterBackup();
                return;
            }

            WorkspaceFileCleanup.RemoveBakFilesOnly(folder, ex.BackupPaths, log);
            MessageBox.Show(
                this,
                "Se descartaron los archivos .bak porque elegiste no conservarlos.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendLog("ERROR: " + ex);
            MessageBox.Show(
                this,
                OutputPathHelper.FormatAccessDeniedMessage(folder, ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (IOException ex)
        {
            AppendLog("ERROR: " + ex);
            MessageBox.Show(
                this,
                OutputPathHelper.FormatIOException(ex, folder),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("ERROR: " + ex);
            MessageBox.Show(
                this,
                UserMessageSpanish.FriendlyError(
                    "No se pudo completar el backup en el servidor SQL (permisos o configuración del motor).",
                    ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex);
            MessageBox.Show(
                this,
                UserMessageSpanish.FriendlyError(
                    "Ocurrió un error durante el backup. Si el problema continúa, probá con menos bases o otra carpeta de destino.",
                    ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _btnRun.Enabled = true;
        }
    }

    private void ResetUiAfterBackup()
    {
        var mgr = ManagerDbName();
        foreach (DataGridViewRow gr in _grid.Rows)
        {
            if (gr.IsNewRow || gr.Tag is not ServerDatabaseRow row)
                continue;

            var isManager = row.PhysicalName.Equals(mgr, StringComparison.OrdinalIgnoreCase);
            row.Selected = isManager;
            gr.Cells["colSel"].Value = isManager ? "Sí" : "No";
        }

        _grid.ClearSelection();
        _grid.Invalidate();

        _suspendEmpresaFilter = true;
        try
        {
            if (_cmbEmpresa.Items.Count > 0)
                _cmbEmpresa.SelectedIndex = 0;
        }
        finally
        {
            _suspendEmpresaFilter = false;
        }

        ApplyRowVisibilityFilter();

        _progress.Value = 0;
        _lblProgressPct.Text = "0 %";
        _lblBackupStatus.Text = "";
    }

    private void ApplyBackupProgress(BackupProgressUpdate u, Stopwatch sw)
    {
        var n = Math.Clamp(u.Percent, 0, 100);
        _progress.Value = n;
        _lblProgressPct.Text = u.IsFinalizing && n >= 90 ? "Finalizando…" : $"{n} %";

        _lblBackupStatus.Text = UiTheme.FormatProgressStatus(u.Status, n, sw);
    }

    private void OpenFolderInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                UserMessageSpanish.FriendlyError(
                    "No se pudo abrir la carpeta en el Explorador de archivos.",
                    ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
