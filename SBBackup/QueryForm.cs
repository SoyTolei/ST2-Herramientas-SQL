using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using SBBackup.Models;
using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

/// <summary>Aplicar una consulta SQL sobre una base, con análisis previo y confirmación transaccional.</summary>
public sealed class QueryForm : Form
{
    private readonly AppConfig _config = AppConfig.Load(AppContext.BaseDirectory);
    private readonly BejermanDatabaseCatalogBuilder _bejermanCatalog;
    private readonly List<ServerDatabaseRow> _databaseRows = [];
    private readonly QueryRunnerCoordinator _runner = new();
    private readonly QueryAiExplainer _ai = new();
    private readonly BackupCoordinator _backup = new(new TransactionLogService());

    private Label _lblConnStatus = null!;
    private WheelSafeComboBox _cmbEmpresa = null!;
    private WheelSafeComboBox _cmbDatabase = null!;
    private DataGridView _grid = null!;
    private Label _lblTargetDb = null!;
    private bool _suspendEmpresaFilter;
    private bool _pinningManager;
    private Button _btnLoadSql = null!;
    private Button _btnFrequent = null!;
    private Button _btnExplainScript = null!;
    private ToolStripDropDown? _frequentMenu;
    private TextBox? _frequentSearch;
    private Panel? _frequentGridHost;
    private ToolStripControlHost? _frequentHost;
    private string? _activeFrequentQueryName;
    private string? _activeFrequentSuccessMessage;
    private bool _bindSelectedDatabase;
    private bool _bindManagerDatabase;
    private TextBox _txtSql = null!;
    private Label _lblFrequentScript = null!;
    private RoundedActionButton _btnRun = null!;
    private Label _lblStatus = null!;
    private OrangeProgressBar _progress = null!;
    private Label _lblPercent = null!;
    private readonly Stopwatch _sw = new();
    private bool _running;
    private string? _lastBackupPath;

    private sealed record EmpresaOption(string? Code, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DatabaseOption(ServerDatabaseRow Row, string Label)
    {
        public override string ToString() => Label;
    }

    public QueryForm()
    {
        _bejermanCatalog = new BejermanDatabaseCatalogBuilder(_config);
        Text = "ST2 · Aplicar Script/Query";
        UiTheme.ApplyDpiAwareScaling(this);
        Font = UiTheme.UiFont();
        BackColor = UiTheme.AppBack;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        MinimumSize = new Size(720, 620);
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
        shell.Controls.Add(UiTheme.CreateSubFormHeader("ST2 · Aplicar Script/Query", (_, _) => Close()), 0, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(12, 10, 12, 12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));   // conexión
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140f));  // base destino
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // editor SQL
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));   // aviso backup
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82f));   // acción + progreso

        _lblConnStatus = UiTheme.CreateConnectionStatusLabel();
        root.Controls.Add(_lblConnStatus, 0, 0);

        root.Controls.Add(BuildDatabaseSection(), 0, 1);
        root.Controls.Add(BuildScriptSection(), 0, 2);

        root.Controls.Add(new Label
        {
            Text = "Si la query modifica datos, se te preguntará si querés hacer un backup de la base antes de aplicarla.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(8.5f, FontStyle.Italic),
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 0, 0)
        }, 0, 3);

        root.Controls.Add(BuildActionBar(), 0, 4);

        shell.Controls.Add(root, 0, 1);
        Controls.Add(shell);

        FormClosed += (_, _) =>
        {
            _ai.Dispose();
            _frequentMenu?.Dispose();
        };

        Shown += async (_, _) =>
        {
            await ApplySessionStateAsync().ConfigureAwait(true);
        };
        UiTheme.BindSubFormScreenSizing(this, 820, 700);
    }

    private Control BuildDatabaseSection()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 0)
        };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));

        // Se conserva internamente en "Todas" para reutilizar el catálogo existente,
        // pero la elección visible se hace directamente por base.
        _cmbEmpresa = new WheelSafeComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Visible = false
        };
        _cmbEmpresa.Items.Add(new EmpresaOption(null, "Todas"));
        _cmbEmpresa.SelectedIndex = 0;

        var databasePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(0, 0, 0, 4)
        };
        var databaseLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        databaseLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
        databaseLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        databaseLayout.Controls.Add(new Label
        {
            Text = "Seleccioná la base",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.25f, FontStyle.Bold),
            Padding = new Padding(2, 0, 0, 2)
        }, 0, 0);

        _cmbDatabase = new WheelSafeComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled = false
        };
        UiTheme.StyleEmpresaCombo(_cmbDatabase);
        _cmbDatabase.SelectedIndexChanged += (_, _) =>
        {
            ApplySelectedDatabaseToSql();
            UpdateTargetDatabaseLabel();
            UpdateRunEnabled();
        };
        databaseLayout.Controls.Add(_cmbDatabase, 0, 1);
        databasePanel.Controls.Add(databaseLayout);
        host.Controls.Add(databasePanel, 0, 0);

        var targetBar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.IncludeYesBg,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 0, 4)
        };
        targetBar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, targetBar.Width - 1, targetBar.Height - 1);
            if (r.Width <= 4)
                return;
            using var path = UiTheme.RoundedRectangle(r, 8);
            using var fill = new SolidBrush(UiTheme.IncludeYesBg);
            e.Graphics.FillPath(fill, path);
            using var pen = new Pen(UiTheme.IncludeYesFg, 1f);
            e.Graphics.DrawPath(pen, path);
        };
        _lblTargetDb = new Label
        {
            Text = "Base a ejecutar: (ninguna seleccionada)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.IncludeYesFg,
            Font = UiTheme.UiFont(10f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Padding = new Padding(2, 0, 0, 0),
            AutoEllipsis = true
        };
        targetBar.Controls.Add(_lblTargetDb);
        host.Controls.Add(targetBar, 0, 1);

        // Se conserva como soporte interno del catálogo y ordenamiento.
        _grid = CreateDatabaseGrid();

        return CardSection.CreateFill("Destino de la consulta", host, new Padding(0, 0, 0, 4));
    }

    private DataGridView CreateDatabaseGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Margin = new Padding(0)
        };
        UiTheme.StyleDataGrid(grid);
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.RowTemplate.Height = UiTheme.GridRowHeight;
        grid.RowTemplate.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.CellFormatting += Grid_CellFormatting;
        grid.Paint += Grid_Paint_EmptyState;
        grid.SortCompare += Grid_SortCompare;
        grid.Sorted += (_, _) => PinManagerRowFirst();
        grid.SelectionChanged += (_, _) =>
        {
            ApplySelectedDatabaseToSql();
            UpdateTargetDatabaseLabel();
            UpdateRunEnabled();
        };
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colTipo",
            HeaderText = "Tipo",
            ReadOnly = true,
            FillWeight = 24,
            MinimumWidth = 120,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colNombre",
            HeaderText = "Nombre",
            ReadOnly = true,
            FillWeight = 46,
            MinimumWidth = 160,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "colDb",
            HeaderText = "Base en SQL Server",
            ReadOnly = true,
            FillWeight = 30,
            MinimumWidth = 120,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });

        return grid;
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

    private void Grid_Paint_EmptyState(object? sender, PaintEventArgs e)
    {
        if (sender is not DataGridView grid || grid.Rows.Count > 0)
            return;

        var msg = AppSession.IsConnected
            ? "No se encontraron bases para mostrar"
            : "Conectá para ver las bases";

        TextRenderer.DrawText(
            e.Graphics, msg, UiTheme.UiFont(10.5f), grid.ClientRectangle, UiTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.ColumnIndex < 0)
            return;

        var colName = grid.Columns[e.ColumnIndex].Name;
        if (colName == "colDb" && e.CellStyle is not null)
            e.CellStyle.Font = UiTheme.UiFont(9.5f, FontStyle.Bold);
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
            "colNombre" => string.Compare(a.GridNombre, b.GridNombre, StringComparison.CurrentCultureIgnoreCase),
            "colTipo" => string.Compare(a.GridTipo, b.GridTipo, StringComparison.CurrentCultureIgnoreCase),
            "colDb" => string.Compare(a.PhysicalName, b.PhysicalName, StringComparison.OrdinalIgnoreCase),
            _ => BejermanDatabaseRules.CompareDatabaseDisplayOrder(a, b, ManagerDbName())
        };
        e.Handled = true;
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

    private void PopulateEmpresaCombo()
    {
        _suspendEmpresaFilter = true;
        try
        {
            _cmbEmpresa.Items.Clear();
            _cmbEmpresa.Items.Add(new EmpresaOption(null, "Todas"));
            var groups = _databaseRows
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

    private void ApplyRowVisibilityFilter()
    {
        if (_cmbEmpresa.Items.Count == 0)
            return;

        var opt = _suspendEmpresaFilter ? null : _cmbEmpresa.SelectedItem as EmpresaOption;
        var code = opt?.Code;
        PopulateDatabaseCombo(code, GetSelectedDatabaseName());
    }

    private void PopulateDatabaseCombo(string? companyCode, string? preferredDatabase)
    {
        var options = _databaseRows
            .Where(r => companyCode is null
                || (r.MatchedEmpCode is not null
                    && r.MatchedEmpCode.Equals(companyCode, StringComparison.OrdinalIgnoreCase)))
            .Select(r => new DatabaseOption(r, FormatDatabaseOption(r)))
            .ToList();

        _cmbDatabase.BeginUpdate();
        try
        {
            _cmbDatabase.Items.Clear();
            foreach (var option in options)
                _cmbDatabase.Items.Add(option);

            var selectedIndex = options.FindIndex(o =>
                !string.IsNullOrWhiteSpace(preferredDatabase)
                && o.Row.PhysicalName.Equals(preferredDatabase, StringComparison.OrdinalIgnoreCase));
            _cmbDatabase.SelectedIndex = selectedIndex >= 0
                ? selectedIndex
                : (_cmbDatabase.Items.Count > 0 ? 0 : -1);
        }
        finally
        {
            _cmbDatabase.EndUpdate();
        }

        var maxWidth = 480;
        if (_cmbDatabase.Items.Count > 0)
        {
            using var graphics = _cmbDatabase.CreateGraphics();
            foreach (var item in _cmbDatabase.Items)
            {
                var width = (int)Math.Ceiling(
                    graphics.MeasureString(item?.ToString() ?? "", _cmbDatabase.Font).Width) + 56;
                maxWidth = Math.Max(maxWidth, width);
            }
        }
        _cmbDatabase.DropDownWidth = Math.Min(maxWidth, 760);
        _cmbDatabase.Enabled = AppSession.IsConnected && !_running && _cmbDatabase.Items.Count > 0;
        UpdateTargetDatabaseLabel();
    }

    private static string FormatDatabaseOption(ServerDatabaseRow row)
    {
        var description = !string.IsNullOrWhiteSpace(row.GridNombre)
            ? row.GridNombre
            : row.FriendlyName;
        var type = string.IsNullOrWhiteSpace(row.GridTipo) ? "" : row.GridTipo + " · ";
        return string.IsNullOrWhiteSpace(description)
            ? row.PhysicalName
            : $"{type}{description}  [{row.PhysicalName}]";
    }

    private void EnsureGridSelection()
    {
        var empresaFiltered = (_cmbEmpresa.SelectedItem as EmpresaOption)?.Code is not null;
        var mgr = ManagerDbName();

        if (_grid.CurrentRow is { Visible: true, Tag: ServerDatabaseRow cur }
            && !(empresaFiltered && cur.PhysicalName.Equals(mgr, StringComparison.OrdinalIgnoreCase)))
        {
            UpdateTargetDatabaseLabel();
            return;
        }

        DataGridViewRow? preferred = null;
        DataGridViewRow? fallback = null;
        foreach (DataGridViewRow gr in _grid.Rows)
        {
            if (gr.IsNewRow || !gr.Visible || gr.Tag is not ServerDatabaseRow row)
                continue;

            fallback ??= gr;
            var isMgr = row.PhysicalName.Equals(mgr, StringComparison.OrdinalIgnoreCase);
            if (empresaFiltered && isMgr)
                continue;

            preferred = gr;
            break;
        }

        var pick = preferred ?? fallback;
        if (pick is null)
        {
            _grid.ClearSelection();
            UpdateTargetDatabaseLabel();
            return;
        }

        _grid.ClearSelection();
        pick.Selected = true;
        _grid.CurrentCell = pick.Cells[0];
        UpdateTargetDatabaseLabel();
    }

    private void UpdateTargetDatabaseLabel()
    {
        if (_lblTargetDb is null)
            return;

        if (_bindManagerDatabase)
        {
            var manager = ManagerDbName();
            _lblTargetDb.Text = $"Base a ejecutar:  {manager}  ·  fija (MANAGER)";
            _lblTargetDb.ForeColor = UiTheme.IncludeYesFg;
            return;
        }

        if (_cmbDatabase.SelectedItem is DatabaseOption selected
            && !string.IsNullOrWhiteSpace(selected.Row.PhysicalName))
        {
            var row = selected.Row;
            var detail = !string.IsNullOrWhiteSpace(row.GridNombre)
                ? row.GridNombre
                : row.FriendlyName;
            _lblTargetDb.Text = string.IsNullOrWhiteSpace(detail)
                || detail.Equals(row.PhysicalName, StringComparison.OrdinalIgnoreCase)
                ? $"Base a ejecutar:  {row.PhysicalName}"
                : $"Base a ejecutar:  {row.PhysicalName}  —  {detail}";
            _lblTargetDb.ForeColor = UiTheme.IncludeYesFg;
        }
        else
        {
            _lblTargetDb.Text = "Base a ejecutar:  (ninguna seleccionada)";
            _lblTargetDb.ForeColor = UiTheme.TextMuted;
        }
    }

    private void ApplyManagerDatabaseToSql()
    {
        var db = ManagerDbName();
        var updated = SubstituteSelectedDatabase(_txtSql.Text, db);
        if (!string.Equals(updated, _txtSql.Text, StringComparison.Ordinal))
        {
            var caret = _txtSql.SelectionStart;
            _txtSql.Text = updated;
            _txtSql.SelectionStart = Math.Min(caret, _txtSql.TextLength);
        }
    }

    private void SelectGridRowByPhysicalName(string physicalName)
    {
        foreach (DataGridViewRow gr in _grid.Rows)
        {
            if (gr.IsNewRow || !gr.Visible || gr.Tag is not ServerDatabaseRow r)
                continue;
            if (!r.PhysicalName.Equals(physicalName, StringComparison.OrdinalIgnoreCase))
                continue;

            _grid.ClearSelection();
            gr.Selected = true;
            _grid.CurrentCell = gr.Cells[0];
            return;
        }

        EnsureGridSelection();
    }

    private static string ManagerDbName(AppConfig config) =>
        BejermanDatabaseRules.ResolveManagerDbName(config);

    private string ManagerDbName() => ManagerDbName(_config);

    private Control BuildScriptSection()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Margin = new Padding(0, 0, 0, 6)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.SectionTitleHeight));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.AppBack,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        titleRow.Controls.Add(new Label
        {
            Text = "Script/Query a ejecutar:",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(4, 0, 6, 2),
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.SectionTitleFont(),
            BackColor = UiTheme.AppBack
        }, 0, 0);

        _lblFrequentScript = new Label
        {
            Text = "",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 2),
            ForeColor = UiTheme.PrimaryDark,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            BackColor = UiTheme.AppBack,
            AutoEllipsis = true
        };
        titleRow.Controls.Add(_lblFrequentScript, 1, 0);
        outer.Controls.Add(titleRow, 0, 0);

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(4),
            Margin = new Padding(0)
        };
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            if (rect.Width <= 2 || rect.Height <= 2)
                return;
            using var path = UiTheme.RoundedRectangle(rect, 8);
            using var pen = new Pen(UiTheme.Border, 1f);
            e.Graphics.DrawPath(pen, path);
        };
        var editor = BuildEditor();
        editor.Dock = DockStyle.Fill;
        card.Controls.Add(editor);
        outer.Controls.Add(card, 0, 1);
        return outer;
    }

    private void SetFrequentScriptTitle(string? name)
    {
        if (_lblFrequentScript is null)
            return;

        if (string.IsNullOrWhiteSpace(name))
        {
            _lblFrequentScript.Text = "";
            return;
        }

        _lblFrequentScript.Text = name;
        _lblFrequentScript.ForeColor = UiTheme.PrimaryDark;
    }

    private Control BuildEditor()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4),
            Padding = Padding.Empty
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var scriptActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            WrapContents = false
        };

        _btnFrequent = MakeToolButton("Frecuentes  ▾", 130);
        _btnFrequent.Click += (_, _) => ShowFrequentMenu();
        scriptActions.Controls.Add(_btnFrequent);

        _btnLoadSql = MakeToolButton("Adjuntar script…", 145);
        _btnLoadSql.Click += (_, _) => LoadSqlFile();
        scriptActions.Controls.Add(_btnLoadSql);

        _btnExplainScript = MakeAiToolButton();
        _btnExplainScript.Click += (_, _) => ExplainScriptInPopup();

        toolbar.Controls.Add(scriptActions, 0, 0);
        toolbar.Controls.Add(_btnExplainScript, 1, 0);
        host.Controls.Add(toolbar, 0, 0);

        _txtSql = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsTab = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextPrimary,
            Font = new Font("Consolas", 9.75f),
            Margin = new Padding(0),
            PlaceholderText = "Pegá aquí la query, o adjuntá un archivo .sql (SELECT, UPDATE, DELETE, etc.)…"
        };
        _txtSql.TextChanged += (_, _) => UpdateRunEnabled();
        host.Controls.Add(_txtSql, 0, 1);

        return host;
    }

    private static Button MakeToolButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(width, 28),
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseCompatibleTextRendering = true
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = UiTheme.Border;
        b.MouseEnter += (_, _) =>
        {
            b.BackColor = UiTheme.PrimarySoft;
            b.FlatAppearance.BorderColor = UiTheme.Primary;
        };
        b.MouseLeave += (_, _) =>
        {
            b.BackColor = UiTheme.Surface;
            b.FlatAppearance.BorderColor = UiTheme.Border;
        };
        return b;
    }

    private static Button MakeAiToolButton()
    {
        var normal = Color.FromArgb(91, 74, 183);
        var hover = Color.FromArgb(68, 54, 153);
        var button = new Button
        {
            Text = "IA · ¿Qué hace este script?",
            AutoSize = false,
            Size = new Size(205, 28),
            Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = normal,
            ForeColor = Color.White,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseCompatibleTextRendering = true
        };
        button.FlatAppearance.BorderSize = 0;
        button.MouseEnter += (_, _) => button.BackColor = hover;
        button.MouseLeave += (_, _) => button.BackColor = normal;
        return button;
    }

    private void LoadSqlFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Elegí el archivo .sql",
            Filter = "Scripts SQL (*.sql)|*.sql|Archivos de texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var content = File.ReadAllText(dlg.FileName);
            _activeFrequentQueryName = null;
            _activeFrequentSuccessMessage = null;
            SetFrequentScriptTitle(null);
            _bindSelectedDatabase = false;
            _bindManagerDatabase = false;
            _txtSql.Text = content;
            _lblStatus.Text = $"Archivo cargado: {Path.GetFileName(dlg.FileName)}";
            _lblStatus.ForeColor = UiTheme.TextMuted;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo leer el archivo:\r\n\r\n" + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowFrequentMenu()
    {
        // Recrear siempre: evita quedar con un menú viejo mal dimensionado en caché.
        _frequentMenu?.Dispose();
        _frequentMenu = BuildFrequentMenu();
        _frequentMenu.Show(_btnFrequent, new Point(0, _btnFrequent.Height));
        _frequentSearch?.Focus();
    }

    private ToolStripDropDown BuildFrequentMenu()
    {
        var outer = new TableLayoutPanel
        {
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var searchWrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(10, 6, 10, 4)
        };
        _frequentSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = UiTheme.UiFont(9f),
            PlaceholderText = "Buscar…"
        };
        searchWrap.Controls.Add(_frequentSearch);

        _frequentGridHost = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        outer.Controls.Add(searchWrap, 0, 0);
        outer.Controls.Add(_frequentGridHost, 0, 1);

        _frequentHost = new ToolStripControlHost(outer)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var dropDown = new ToolStripDropDown
        {
            AutoSize = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = UiTheme.Surface,
            DropShadowEnabled = true,
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new FrequentMenuRenderer()
        };
        dropDown.Items.Add(_frequentHost);

        // Filtro en vivo: no cierra el menú, solo reconstruye la grilla de abajo.
        _frequentSearch.TextChanged += (_, _) => RefreshFrequentGrid(outer, dropDown, _frequentSearch.Text);
        _frequentSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                _frequentMenu?.Close();
                e.Handled = true;
            }
        };

        RefreshFrequentGrid(outer, dropDown, string.Empty);
        return dropDown;
    }

    private void RefreshFrequentGrid(TableLayoutPanel outer, ToolStripDropDown dropDown, string filter)
    {
        if (_frequentGridHost is null || _frequentHost is null)
            return;

        _frequentGridHost.SuspendLayout();
        foreach (Control old in _frequentGridHost.Controls)
            old.Dispose();
        _frequentGridHost.Controls.Clear();

        var content = BuildFrequentGrid(filter, out var gridW, out var gridH);
        _frequentGridHost.Controls.Add(content);
        _frequentGridHost.Size = new Size(gridW, gridH);
        _frequentGridHost.ResumeLayout(true);

        const int searchRowHeight = 34;
        var totalW = Math.Max(gridW, 260);
        var totalH = searchRowHeight + gridH;

        outer.Size = new Size(totalW, totalH);
        _frequentHost.Size = new Size(totalW, totalH);
        dropDown.Size = new Size(totalW, totalH);
    }

    private Control BuildFrequentGrid(string filter, out int totalW, out int totalH)
    {
        var normalizedFilter = NormalizeForSearch(filter);
        var matchedGroups = new List<FrequentQueries.Group>();
        foreach (var g in FrequentQueries.Groups)
        {
            if (normalizedFilter.Length == 0)
            {
                matchedGroups.Add(g);
                continue;
            }

            var groupNameMatches = NormalizeForSearch(g.Name).Contains(normalizedFilter);
            var matchedItems = groupNameMatches
                ? g.Items
                : g.Items.Where(it => NormalizeForSearch(it.Name).Contains(normalizedFilter)).ToList();
            if (matchedItems.Count > 0)
                matchedGroups.Add(new FrequentQueries.Group(g.Name, matchedItems));
        }

        if (matchedGroups.Count == 0)
        {
            var empty = new Label
            {
                Text = $"Sin resultados para «{filter}».",
                AutoSize = false,
                Size = new Size(280, 44),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = UiTheme.TextMuted,
                Font = UiTheme.UiFont(9f),
                BackColor = UiTheme.Surface,
                Padding = new Padding(10)
            };
            totalW = 280;
            totalH = 44;
            return empty;
        }

        const int columns = 2;
        var rows = (matchedGroups.Count + columns - 1) / columns;

        var colWidths = new int[columns];
        using (var itemFont = UiTheme.UiFont(9f))
        using (var titleFont = UiTheme.UiFont(8.25f, FontStyle.Bold))
        {
            for (var i = 0; i < matchedGroups.Count; i++)
            {
                var col = i % columns;
                var needed = TextRenderer.MeasureText(
                    matchedGroups[i].Name.ToUpperInvariant(),
                    titleFont,
                    new Size(int.MaxValue, 0),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

                foreach (var item in matchedGroups[i].Items)
                {
                    var w = TextRenderer.MeasureText(
                        item.Name,
                        itemFont,
                        new Size(int.MaxValue, 0),
                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    if (w > needed)
                        needed = w;
                }

                // Margen interno + padding del label (sin restar pixels: eso era lo que cortaba).
                colWidths[col] = Math.Max(colWidths[col], needed + 36);
            }
        }

        for (var c = 0; c < columns; c++)
            colWidths[c] = Math.Clamp(colWidths[c], 220, 480);

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            RowCount = rows,
            BackColor = UiTheme.Surface,
            Padding = new Padding(10, 6, 10, 8),
            Margin = Padding.Empty
        };
        for (var c = 0; c < columns; c++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, colWidths[c]));
        for (var r = 0; r < rows; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        for (var i = 0; i < matchedGroups.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;
            grid.Controls.Add(BuildFrequentGroupPanel(matchedGroups[i], colWidths[col]), col, row);
        }

        var sumW = grid.Padding.Left + grid.Padding.Right;
        foreach (var cw in colWidths)
            sumW += cw;

        var rowHeights = new int[rows];
        for (var i = 0; i < matchedGroups.Count; i++)
        {
            var row = i / columns;
            var rowH = 30 + matchedGroups[i].Items.Count * 24;
            if (rowH > rowHeights[row])
                rowHeights[row] = rowH;
        }

        var sumH = grid.Padding.Top + grid.Padding.Bottom;
        foreach (var rh in rowHeights)
            sumH += rh;

        grid.MinimumSize = new Size(sumW, sumH);
        grid.Size = new Size(sumW, sumH);

        totalW = sumW;
        totalH = sumH;
        return grid;
    }

    private static string NormalizeForSearch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var formD = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private Control BuildFrequentGroupPanel(FrequentQueries.Group group, int columnWidth)
    {
        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1 + group.Items.Count,
            BackColor = UiTheme.Surface,
            Margin = new Padding(4, 2, 4, 6),
            Padding = new Padding(2, 2, 2, 2),
            Width = columnWidth - 8
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, columnWidth - 12f));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < group.Items.Count; i++)
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        stack.Controls.Add(new Label
        {
            Text = group.Name.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = UiTheme.PrimaryDark,
            Font = UiTheme.UiFont(8.25f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Margin = new Padding(4, 2, 4, 4),
            Padding = Padding.Empty
        }, 0, 0);

        var itemWidth = Math.Max(80, columnWidth - 16);
        for (var i = 0; i < group.Items.Count; i++)
        {
            var item = group.Items[i];
            var captured = item;
            var lbl = new Label
            {
                Text = item.Name,
                AutoSize = false,
                Size = new Size(itemWidth, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.TextPrimary,
                Font = UiTheme.UiFont(9f),
                Cursor = Cursors.Hand,
                BackColor = UiTheme.Surface,
                Margin = new Padding(2, 1, 2, 1),
                Padding = new Padding(4, 0, 4, 0),
                AutoEllipsis = false
            };
            if (!string.IsNullOrWhiteSpace(item.Note))
            {
                var tip = new ToolTip { ShowAlways = true, AutoPopDelay = 8000 };
                tip.SetToolTip(lbl, item.Note);
            }

            lbl.Click += (_, _) =>
            {
                _frequentMenu?.Close();
                ApplyFrequentQuery(captured);
            };
            lbl.MouseEnter += (_, _) =>
            {
                lbl.ForeColor = UiTheme.PrimaryDark;
                lbl.BackColor = UiTheme.PrimaryLight;
            };
            lbl.MouseLeave += (_, _) =>
            {
                lbl.ForeColor = UiTheme.TextPrimary;
                lbl.BackColor = UiTheme.Surface;
            };
            stack.Controls.Add(lbl, 0, i + 1);
        }

        return stack;
    }

    private void ApplyFrequentQuery(FrequentQueries.Item item)
    {
        _activeFrequentQueryName = item.Name;
        _activeFrequentSuccessMessage = item.SuccessMessage;
        SetFrequentScriptTitle(item.Name);
        _bindManagerDatabase = item.UsesManagerDatabase;
        _bindSelectedDatabase = item.UsesSelectedDatabase && !item.UsesManagerDatabase;
        _txtSql.Text = item.Sql;

        if (_bindManagerDatabase)
            ApplyManagerDatabaseToSql();
        else if (_bindSelectedDatabase)
            ApplySelectedDatabaseToSql();

        UpdateTargetDatabaseLabel();

        _txtSql.SelectionStart = _txtSql.TextLength;

        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            _lblStatus.ForeColor = UiTheme.PrimaryDark;
            _lblStatus.Text = item.Note;
        }
        else
        {
            _lblStatus.ForeColor = UiTheme.TextMuted;
            _lblStatus.Text = $"Consulta cargada: {item.Name}";
        }

        _txtSql.Focus();
    }

    /// <summary>
    /// Cuando hay una consulta frecuente vinculada a la base elegida, reescribe
    /// los placeholders («use …», «&lt;baseDeDatos&gt;») con la base de la grilla.
    /// </summary>
    private string? GetSelectedDatabaseName()
    {
        return (_cmbDatabase.SelectedItem as DatabaseOption)?.Row.PhysicalName;
    }

    private void ApplySelectedDatabaseToSql()
    {
        if (_bindManagerDatabase)
            return;

        if (!_bindSelectedDatabase)
            return;

        var db = GetSelectedDatabaseName() ?? "";
        var updated = SubstituteSelectedDatabase(_txtSql.Text, db);
        if (!string.Equals(updated, _txtSql.Text, StringComparison.Ordinal))
        {
            var caret = _txtSql.SelectionStart;
            _txtSql.Text = updated;
            _txtSql.SelectionStart = Math.Min(caret, _txtSql.TextLength);
        }
    }

    private static string SubstituteSelectedDatabase(string sql, string database)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var result = sql;

        if (result.Contains("<baseDeDatos>", StringComparison.Ordinal))
        {
            var name = string.IsNullOrWhiteSpace(database) ? "<baseDeDatos>" : database;
            result = result.Replace("<baseDeDatos>", name, StringComparison.Ordinal);
        }

        if (Regex.IsMatch(result, @"^\s*use\s+\S", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            result = RewriteUseLine(result, database);

        return result;
    }

    private static string RewriteUseLine(string sql, string database)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var target = string.IsNullOrWhiteSpace(database)
            ? "use SBDA<CODIGO>"
            : $"use [{database}]";

        var lines = sql.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\s*use\s+\S", RegexOptions.IgnoreCase))
            {
                lines[i] = target;
                break;
            }
        }

        return string.Join("\r\n", lines);
    }

    private void ExpandEditor()
    {
        using var win = new Form
        {
            Text = "Script/Query — vista ampliada",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(820, 600),
            MinimumSize = new Size(480, 320),
            BackColor = UiTheme.AppBack,
            Font = UiTheme.UiFont(),
            ShowInTaskbar = false,
            MinimizeBox = false
        };

        var big = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsTab = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextPrimary,
            Font = new Font("Consolas", 11f),
            Text = _txtSql.Text
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.AppBack, Padding = new Padding(10, 8, 10, 8) };
        var ok = new Button { Text = "Aceptar", Dock = DockStyle.Right, Width = 120 };
        UiTheme.StylePickFileButton(ok);
        ok.DialogResult = DialogResult.OK;
        bottom.Controls.Add(ok);

        win.Controls.Add(big);
        win.Controls.Add(bottom);
        win.AcceptButton = ok;

        if (win.ShowDialog(this) == DialogResult.OK)
            _txtSql.Text = big.Text;
    }

    private Control BuildActionBar()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.AppBack,
            Margin = new Padding(0)
        };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));

        _lblPercent = new Label
        {
            Text = "0 %",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(8.75f, FontStyle.Bold),
            BackColor = Color.Transparent
        };

        _progress = new OrangeProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 100,
            MaxBarHeight = 16,
            Margin = new Padding(0, 4, 8, 3)
        };

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(0, 2, 0, 4),
            Margin = Padding.Empty
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _btnRun = new RoundedActionButton
        {
            Text = "Ejecutar query",
            Enabled = false,
            AutoSize = false,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(160, 42)
        };
        UiTheme.StyleRestoreButton(_btnRun);
        _btnRun.Margin = new Padding(0, 0, 10, 0);
        _btnRun.Click += async (_, _) => await RunQueryAsync().ConfigureAwait(true);
        actionRow.Controls.Add(_btnRun, 0, 0);

        _lblStatus = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Margin = new Padding(4, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.25f)
        };
        actionRow.Controls.Add(_lblStatus, 1, 0);

        var progressRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.AppBack,
            Margin = Padding.Empty
        };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48f));
        progressRow.Controls.Add(_progress, 0, 0);
        progressRow.Controls.Add(_lblPercent, 1, 0);

        host.Controls.Add(actionRow, 0, 0);
        host.Controls.Add(progressRow, 0, 1);

        return host;
    }

    private void SetProgress(int percent, string detail)
    {
        var n = Math.Clamp(percent, 0, 100);
        _progress.Value = n;
        _lblPercent.Text = n + " %";
        _lblStatus.ForeColor = UiTheme.TextMuted;
        _lblStatus.Text = UiTheme.FormatProgressStatus(detail, n, _sw);
    }

    private async Task ApplySessionStateAsync()
    {
        UiTheme.SetConnectionStatusLabel(_lblConnStatus, AppSession.IsConnected, AppSession.ServerName);
        var connected = AppSession.IsConnected && !_running;
        _cmbEmpresa.Enabled = connected;
        _cmbDatabase.Enabled = connected && _cmbDatabase.Items.Count > 0;
        _txtSql.Enabled = connected;
        _btnExplainScript.Enabled = connected;
        UpdateRunEnabled();

        if (AppSession.IsConnected)
            await LoadDatabasesAsync().ConfigureAwait(true);
    }

    private async Task LoadDatabasesAsync()
    {
        if (string.IsNullOrEmpty(AppSession.ConnectionString))
            return;

        var currentPhysical = GetSelectedDatabaseName();
        try
        {
            var rows = await _bejermanCatalog
                .LoadAsync(AppSession.ConnectionString, null, CancellationToken.None)
                .ConfigureAwait(true);

            _databaseRows.Clear();
            _databaseRows.AddRange(rows);

            _grid.Rows.Clear();
            foreach (var row in rows)
            {
                var i = _grid.Rows.Add(row.GridTipo, row.GridNombre, row.PhysicalName);
                _grid.Rows[i].Tag = row;
            }

            PopulateDatabaseCombo(companyCode: null, preferredDatabase: currentPhysical);
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "No se pudo listar las bases: " + ex.Message;
        }
    }

    private void UpdateRunEnabled()
    {
        _btnRun.Enabled = AppSession.IsConnected
            && !_running
            && !string.IsNullOrWhiteSpace(_txtSql.Text)
            && HasExecutionDatabase();
    }

    private bool HasExecutionDatabase() =>
        _bindManagerDatabase
            ? !string.IsNullOrWhiteSpace(ManagerDbName())
            : !string.IsNullOrWhiteSpace(GetSelectedDatabaseName());

    private QueryAnalyzer.Analysis AnalyzeSql(bool showEmptyWarning)
    {
        var analysis = QueryAnalyzer.Analyze(_txtSql.Text);

        if (analysis.IsEmpty && showEmptyWarning)
        {
            MessageBox.Show(this, "Pegá una query antes de analizar.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return analysis;
    }

    /// <summary>WinForms sólo muestra saltos de línea con \r\n; normalizamos cualquier \n suelto.</summary>
    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    /// <summary>Limpia markdown y asegura una línea en blanco antes de cada sección conocida.</summary>
    private static string FormatAiExplanation(string raw)
    {
        var text = raw.Replace("**", "").Replace("*", "").Replace("`", "").Trim();
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        string[] labels =
        [
            "Qué hace:",
            "Que hace:",
            "Sobre qué actúa:",
            "Tipo de acción:",
            "Precauciones:",
            "Tablas afectadas:",
            "Tipo:",
            "Riesgos:",
            "En criollo:",
            "Sobre qué datos:",
            "Qué tipo de acción es:",
            "Ojo, cuidado:"
        ];
        foreach (var label in labels)
            text = text.Replace("\n" + label, "\n\n" + label);

        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");

        return NormalizeNewlines(text.Trim());
    }

    private void ExplainScriptInPopup()
    {
        if (_running)
            return;

        if (string.IsNullOrWhiteSpace(_txtSql.Text))
        {
            MessageBox.Show(this, "Pegá o cargá un script antes de pedirle la explicación.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_ai.IsConfigured)
        {
            MessageBox.Show(this, "La explicación con IA no está configurada.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new Form
        {
            Text = "ST2 · ¿Qué hace este script?",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 360),
            MinimumSize = new Size(480, 300),
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = UiTheme.AppBack,
            Font = UiTheme.UiFont()
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = UiTheme.AppBack
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

        root.Controls.Add(new Label
        {
            Text = "Explicación del script",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(11f, FontStyle.Bold),
            BackColor = Color.Transparent
        }, 0, 0);

        var explanationBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.5f),
            Margin = new Padding(0, 6, 0, 8),
            Text = "Consultando a la IA…"
        };
        root.Controls.Add(explanationBox, 0, 1);

        var closeButton = CreateDialogButton("Cerrar", DialogResult.OK, Padding.Empty);
        closeButton.Dock = DockStyle.Right;
        closeButton.Width = 110;
        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(0, 4, 0, 0)
        };
        footer.Controls.Add(closeButton);
        root.Controls.Add(footer, 0, 2);

        dialog.AcceptButton = closeButton;
        dialog.CancelButton = closeButton;
        dialog.Controls.Add(root);

        dialog.Shown += async (_, _) =>
        {
            _btnExplainScript.Enabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var dbForAi = _bindManagerDatabase
                    ? ManagerDbName()
                    : GetSelectedDatabaseName() ?? "";
                // Leemos el esquema real (tablas/columnas) de la base conectada para que la IA
                // no tenga que adivinar: es la fuente de verdad de esta instalación puntual.
                var tableNames = SchemaContextBuilder.ExtractTableNames(_txtSql.Text);
                var schemaContext = await SchemaContextBuilder
                    .BuildAsync(AppSession.ConnectionString, dbForAi, tableNames, cts.Token)
                    .ConfigureAwait(true);
                var explanation = await _ai
                    .ExplicarAsync(_txtSql.Text, dbForAi, schemaContext, cts.Token)
                    .ConfigureAwait(true);

                if (dialog.IsDisposed)
                    return;

                explanationBox.ForeColor = UiTheme.TextPrimary;
                explanationBox.Text = FormatAiExplanation(explanation);
                explanationBox.SelectionStart = 0;
                explanationBox.SelectionLength = 0;
            }
            catch (Exception ex)
            {
                if (dialog.IsDisposed)
                    return;

                explanationBox.ForeColor = UiTheme.TextMuted;
                explanationBox.Text =
                    "No se pudo obtener la explicación.\r\n\r\n" +
                    ex.Message +
                    "\r\n\r\nVerificá que la PC tenga acceso a internet.";
            }
            finally
            {
                if (!IsDisposed)
                    _btnExplainScript.Enabled = !_running;
            }
        };

        dialog.ShowDialog(this);
    }

    private async Task RunQueryAsync()
    {
        if (_running)
            return;

        if (!AppSession.IsConnected || string.IsNullOrEmpty(AppSession.ConnectionString))
        {
            MessageBox.Show(this, "No hay conexión al servidor SQL.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var database = _bindManagerDatabase
            ? ManagerDbName()
            : GetSelectedDatabaseName()?.Trim();
        var sql = _txtSql.Text;
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(sql))
            return;

        var analysis = AnalyzeSql(showEmptyWarning: false);
        if (analysis.IsEmpty)
            return;

        var doBackup = false;
        var useTransaction = analysis.HasModifications && !analysis.RequiresNoTransaction;
        if (analysis.HasModifications)
        {
            var warnDataLoss = sql.Contains("REPAIR_ALLOW_DATA_LOSS", StringComparison.OrdinalIgnoreCase);
            var choice = ShowBackupConfirmation(database, warnDataLoss);

            if (choice == DialogResult.Cancel)
                return;

            doBackup = choice == DialogResult.Yes;
        }

        SetRunning(true);
        _lblStatus.ForeColor = UiTheme.TextMuted;
        _lastBackupPath = null;
        _sw.Restart();
        var totalSteps = doBackup ? 2 : 1;
        SetProgress(0, doBackup
            ? $"Paso 1/{totalSteps} · preparando backup de seguridad…"
            : $"Paso 1/{totalSteps} · preparando ejecución…");

        try
        {
            if (doBackup)
            {
                var backupOk = await TryBackupBeforeAsync(database, totalSteps).ConfigureAwait(true);
                if (!backupOk)
                {
                    var cont = MessageBox.Show(this,
                        "El backup de seguridad no se pudo completar.\r\n\r\n" +
                        "¿Querés ejecutar la query igual, SIN backup previo?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (cont != DialogResult.Yes)
                    {
                        SetProgress(0, "Cancelado: no se ejecutó la query (falló el backup previo).");
                        _lblStatus.ForeColor = UiTheme.TextMuted;
                        return;
                    }
                }
            }

            SetProgress(doBackup ? 88 : 20,
                $"Paso {totalSteps}/{totalSteps} · " + (useTransaction
                    ? "ejecutando en transacción…"
                    : "ejecutando consulta…"));

            var result = await _runner.ExecuteAsync(
                AppSession.ConnectionString,
                database,
                sql,
                useTransaction,
                confirmCommit: null,
                null,
                CancellationToken.None).ConfigureAwait(true);

            _progress.Value = 100;
            _lblPercent.Text = "100 %";

            var summary = BuildResultSummary(result);
            if (_lastBackupPath is not null && (!result.WasTransactional || result.Committed))
                summary += "  ·  Backup previo en: " + BackupFolderText();
            summary += "  ·  " + FormatElapsed();
            _lblStatus.Text = summary;
            _lblStatus.ForeColor = (!result.WasTransactional || result.Committed)
                ? UiTheme.IncludeYesFg
                : UiTheme.TextMuted;

            if (result.ResultSets.Any(t => t.Columns.Count > 0)
                || !string.IsNullOrWhiteSpace(_activeFrequentQueryName))
            {
                ShowResultsWindow(
                    result,
                    database,
                    _activeFrequentQueryName,
                    _activeFrequentSuccessMessage);
            }
        }
        catch (Exception ex)
        {
            SetProgress(0, "Error al ejecutar la query.");
            _lblStatus.ForeColor = UiTheme.Danger;
            _lblStatus.Text = "Error al ejecutar la query.";
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError("La query no se pudo ejecutar. No se aplicó ningún cambio.", ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _sw.Stop();
            SetRunning(false);
        }
    }

    private DialogResult ShowBackupConfirmation(string database, bool warnDataLoss = false)
    {
        var height = warnDataLoss ? 318 : 270;
        using var dialog = new Form
        {
            Text = "ST2 · Confirmar ejecución",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(500, height),
            MinimumSize = new Size(500, height),
            MaximumSize = new Size(500, height),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = UiTheme.AppBack,
            Font = UiTheme.UiFont(),
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96F, 96F)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = warnDataLoss ? 6 : 5,
            Padding = new Padding(20, 12, 20, 14),
            BackColor = UiTheme.AppBack
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
        if (warnDataLoss)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

        var row = 0;
        root.Controls.Add(new Label
        {
            Text = "Esta query realizará cambios",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(12.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        }, 0, row++);

        var databasePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.PrimarySoft,
            Padding = new Padding(12, 5, 12, 5),
            Margin = new Padding(0, 2, 0, 6)
        };
        databasePanel.Controls.Add(new Label
        {
            Text = $"Base de datos destino\r\n{database}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.PrimaryDark,
            Font = UiTheme.UiFont(10f, FontStyle.Bold),
            BackColor = Color.Transparent,
            AutoEllipsis = true
        });
        root.Controls.Add(databasePanel, 0, row++);

        if (warnDataLoss)
        {
            var warnPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(254, 242, 242),
                Padding = new Padding(12, 6, 12, 6),
                Margin = new Padding(0, 0, 0, 4)
            };
            warnPanel.Controls.Add(new Label
            {
                Text = "Esta operación puede provocar pérdida de datos (REPAIR_ALLOW_DATA_LOSS).",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.IncludeNoFg,
                Font = UiTheme.UiFont(9f, FontStyle.Bold),
                BackColor = Color.Transparent
            });
            root.Controls.Add(warnPanel, 0, row++);
        }

        root.Controls.Add(new Label
        {
            Text = "¿Querés realizar un backup antes de aplicar la query?",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(10.25f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Padding = new Padding(0, 5, 0, 4)
        }, 0, row++);

        root.Controls.Add(new Label
        {
            Text = "Si elegís «Ejecutar sin backup», los cambios se aplicarán directamente.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(8.75f),
            BackColor = Color.Transparent,
            Padding = Padding.Empty
        }, 0, row++);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26f));

        var backupButton = new Button
        {
            Text = "Hacer backup",
            DialogResult = DialogResult.Yes,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.Primary,
            ForeColor = Color.White,
            Font = UiTheme.UiFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        backupButton.FlatAppearance.BorderSize = 0;
        backupButton.MouseEnter += (_, _) => backupButton.BackColor = UiTheme.PrimaryDark;
        backupButton.MouseLeave += (_, _) => backupButton.BackColor = UiTheme.Primary;

        var withoutBackupButton = CreateDialogButton(
            "Ejecutar sin backup", DialogResult.No, new Padding(0, 0, 8, 0));
        var cancelButton = CreateDialogButton(
            "Cancelar", DialogResult.Cancel, Padding.Empty);

        actions.Controls.Add(backupButton, 0, 0);
        actions.Controls.Add(withoutBackupButton, 1, 0);
        actions.Controls.Add(cancelButton, 2, 0);
        root.Controls.Add(actions, 0, row);

        dialog.AcceptButton = backupButton;
        dialog.CancelButton = cancelButton;
        dialog.Controls.Add(root);
        return dialog.ShowDialog(this);
    }

    private static Button CreateDialogButton(string text, DialogResult result, Padding margin)
    {
        var button = new Button
        {
            Text = text,
            DialogResult = result,
            Dock = DockStyle.Fill,
            Margin = margin,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = UiTheme.Border;
        button.FlatAppearance.BorderSize = 1;
        button.MouseEnter += (_, _) =>
        {
            button.BackColor = UiTheme.PrimarySoft;
            button.FlatAppearance.BorderColor = UiTheme.Primary;
        };
        button.MouseLeave += (_, _) =>
        {
            button.BackColor = UiTheme.Surface;
            button.FlatAppearance.BorderColor = UiTheme.Border;
        };
        return button;
    }

    private string BackupFolderText()
    {
        if (string.IsNullOrEmpty(_lastBackupPath))
            return "(carpeta de backup)";
        try
        {
            return Path.GetDirectoryName(_lastBackupPath) ?? _lastBackupPath;
        }
        catch
        {
            return _lastBackupPath;
        }
    }

    private string FormatElapsed() => "Tiempo: " + UiTheme.FormatDuration(_sw.Elapsed);

    private void ShowResultsWindow(
        QueryRunnerCoordinator.ExecutionResult result,
        string database,
        string? frequentQueryName,
        string? frequentSuccessMessage)
    {
        var resultSets = result.ResultSets.Where(t => t.Columns.Count > 0).ToList();
        var hasTables = resultSets.Count > 0;
        var totalRows = resultSets.Sum(t => t.Rows.Count);

        var friendlyMessage = !string.IsNullOrWhiteSpace(frequentSuccessMessage)
            ? FrequentQueries.FormatSuccessMessage(
                frequentSuccessMessage,
                database,
                result.RowsAffected,
                totalRows)
            : null;

        var fallbackDetail = result.RowsAffected == 1
            ? "Se modificó 1 fila."
            : result.RowsAffected > 1
                ? $"Se modificaron {result.RowsAffected:N0} filas."
                : hasTables
                    ? $"Consulta finalizada. Filas devueltas: {totalRows:N0}."
                    : "La consulta finalizó correctamente. No se modificaron filas.";

        var messageText = friendlyMessage ?? fallbackDetail;
        var showMessageBanner = hasTables && !string.IsNullOrWhiteSpace(friendlyMessage);

        using var win = new Form
        {
            Text = "ST2 · Resultados de la consulta",
            StartPosition = FormStartPosition.CenterParent,
            Size = hasTables ? new Size(920, 660) : new Size(640, 380),
            MinimumSize = hasTables ? new Size(620, 440) : new Size(520, 320),
            BackColor = UiTheme.AppBack,
            Font = UiTheme.UiFont(),
            ShowInTaskbar = false,
            MinimizeBox = false,
            FormBorderStyle = FormBorderStyle.Sizable
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = showMessageBanner ? 4 : 3,
            Padding = new Padding(16),
            BackColor = UiTheme.AppBack
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78f));
        if (showMessageBanner)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.PrimarySoft,
            Padding = new Padding(14, 8, 14, 8),
            Margin = new Padding(0, 0, 0, 10)
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        header.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(frequentQueryName)
                ? "Consulta ejecutada correctamente"
                : frequentQueryName,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.PrimaryDark,
            Font = UiTheme.UiFont(12f, FontStyle.Bold),
            BackColor = Color.Transparent
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = hasTables
                ? $"Base: {database}   ·   {totalRows} fila(s)"
                    + (resultSets.Count > 1 ? $"   ·   {resultSets.Count} resultados" : "")
                : $"Ejecución correcta   ·   Base: {database}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = UiTheme.UiFont(9.5f),
            BackColor = Color.Transparent
        }, 0, 1);

        var row = 0;
        root.Controls.Add(header, 0, row++);

        if (showMessageBanner)
        {
            var banner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(236, 253, 245),
                Padding = new Padding(14, 10, 14, 10),
                Margin = new Padding(0, 0, 0, 10)
            };
            banner.Controls.Add(new Label
            {
                Text = messageText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.IncludeYesFg,
                Font = UiTheme.UiFont(10f, FontStyle.Bold),
                BackColor = Color.Transparent
            });
            root.Controls.Add(banner, 0, row++);
        }

        Control resultContent;
        if (hasTables)
        {
            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = UiTheme.UiFont(9.5f, FontStyle.Bold),
                Margin = Padding.Empty
            };
            for (var i = 0; i < resultSets.Count; i++)
            {
                var table = resultSets[i];
                var page = new TabPage(resultSets.Count == 1
                    ? $"Resultado · {table.Rows.Count} fila(s)"
                    : $"Resultado {i + 1} · {table.Rows.Count} fila(s)")
                {
                    BackColor = UiTheme.Surface,
                    Padding = new Padding(4)
                };
                var grid = CreateResultGrid(table);
                page.Controls.Add(grid);
                tabs.TabPages.Add(page);
            }
            resultContent = tabs;
        }
        else
        {
            var completion = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = UiTheme.Surface,
                Padding = new Padding(24),
                Margin = Padding.Empty
            };
            completion.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
            completion.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
            completion.Controls.Add(new Label
            {
                Text = "Operación completada",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomCenter,
                ForeColor = UiTheme.IncludeYesFg,
                Font = UiTheme.UiFont(14f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 8)
            }, 0, 0);
            completion.Controls.Add(new Label
            {
                Text = messageText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = UiTheme.TextPrimary,
                Font = UiTheme.UiFont(10.5f),
                BackColor = Color.Transparent,
                Padding = new Padding(12, 4, 12, 0)
            }, 0, 1);
            resultContent = completion;
        }
        root.Controls.Add(resultContent, 0, row++);

        var closeButton = CreateDialogButton("Cerrar", DialogResult.OK, Padding.Empty);
        closeButton.Dock = DockStyle.Right;
        closeButton.Width = 120;
        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.AppBack,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.Controls.Add(closeButton);
        root.Controls.Add(footer, 0, row);

        win.AcceptButton = closeButton;
        win.CancelButton = closeButton;
        win.Controls.Add(root);
        win.ShowDialog(this);
    }

    private static DataGridView CreateResultGrid(DataTable table)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = true,
            AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
            DataSource = table,
            Margin = Padding.Empty
        };
        UiTheme.StyleDataGrid(grid);
        grid.RowTemplate.Height = 26;
        grid.DefaultCellStyle.Font = UiTheme.UiFont(9f);
        grid.AlternatingRowsDefaultCellStyle.Font = UiTheme.UiFont(9f);
        return grid;
    }

    private static string BuildResultSummary(QueryRunnerCoordinator.ExecutionResult result)
    {
        if (result.WasTransactional)
        {
            return result.Committed
                ? $"Cambios aplicados (COMMIT). Filas afectadas: {result.RowsAffected}."
                : "Operación cancelada (ROLLBACK). No se modificó nada.";
        }

        var rows = result.ResultSets.FirstOrDefault()?.Rows.Count ?? 0;
        return result.ResultSets.Count > 0
            ? $"Consulta ejecutada. Filas devueltas: {rows}."
            : "Consulta ejecutada correctamente.";
    }

    private void SetRunning(bool running)
    {
        _running = running;
        var idle = !running;
        _btnRun.Enabled = idle && AppSession.IsConnected
            && !string.IsNullOrWhiteSpace(_txtSql.Text)
            && HasExecutionDatabase();
        _btnExplainScript.Enabled = idle;
        _btnLoadSql.Enabled = idle;
        _btnFrequent.Enabled = idle;
        _txtSql.Enabled = idle;
        _cmbEmpresa.Enabled = idle;
        _cmbDatabase.Enabled = idle && _cmbDatabase.Items.Count > 0;
        Cursor = running ? Cursors.WaitCursor : Cursors.Default;
    }

    private async Task<bool> TryBackupBeforeAsync(string database, int totalSteps)
    {
        if (string.IsNullOrEmpty(AppSession.ConnectionString))
            return false;

        try
        {
            var dir = OutputPathHelper.GetDefaultOutputDirectory();
            var rows = new List<ServerDatabaseRow>
            {
                new() { PhysicalName = database, FriendlyName = "" }
            };

            var lastDetail = "generando .bak…";
            var log = new Progress<string>(_ => { });
            var prog = new Progress<BackupProgressUpdate>(u =>
            {
                if (!string.IsNullOrWhiteSpace(u.Status))
                    lastDetail = u.Status;
                // El backup ocupa el 85 % del progreso total; el 15 % restante es la ejecución.
                var overall = (int)Math.Round(u.Percent * 0.85);
                SetProgress(overall, $"Paso 1/{totalSteps} · backup de seguridad — {lastDetail}");
            });

            SetProgress(2, $"Paso 1/{totalSteps} · backup de seguridad de «{database}»…");

            var result = await _backup.RunBackupsAsync(
                AppSession.ConnectionString,
                rows,
                dir,
                log,
                prog,
                CancellationToken.None,
                includeTimeInName: true).ConfigureAwait(true);

            _lastBackupPath = result.ZipFilePath ?? result.OutputDirectory;
            SetProgress(85, $"Paso 1/{totalSteps} · backup listo en: {BackupFolderText()}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                UserMessageSpanish.FriendlyError("No se pudo hacer el backup de seguridad.", ex),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private sealed class FrequentMenuRenderer : ToolStripProfessionalRenderer
    {
        public FrequentMenuRenderer() : base(new FrequentMenuColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is ToolStripLabel)
            {
                e.TextColor = UiTheme.PrimaryDark;
                base.OnRenderItemText(e);
                return;
            }

            if (e.Item is ToolStripMenuItem && e.Item.Selected)
                e.TextColor = UiTheme.PrimaryDark;

            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is ToolStripSeparator or ToolStripLabel)
                return;

            var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            if (e.Item.Selected)
            {
                using var brush = new SolidBrush(UiTheme.PrimaryLight);
                e.Graphics.FillRectangle(brush, rect);
                using var accent = new Pen(UiTheme.Primary, 2f);
                e.Graphics.DrawLine(accent, rect.Left, rect.Top + 3, rect.Left, rect.Bottom - 3);
            }
            else
            {
                using var brush = new SolidBrush(UiTheme.Surface);
                e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var rect = e.Item.ContentRectangle;
            var y = rect.Top + rect.Height / 2;
            using var pen = new Pen(Color.FromArgb(230, 234, 240));
            e.Graphics.DrawLine(pen, rect.Left + 10, y, rect.Right - 10, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var pen = new Pen(Color.FromArgb(210, 216, 224));
            e.Graphics.DrawRectangle(pen, rect);
        }
    }

    private sealed class FrequentMenuColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(210, 216, 224);
        public override Color ToolStripDropDownBackground => UiTheme.Surface;
        public override Color ImageMarginGradientBegin => UiTheme.Surface;
        public override Color ImageMarginGradientMiddle => UiTheme.Surface;
        public override Color ImageMarginGradientEnd => UiTheme.Surface;
        public override Color MenuItemBorder => UiTheme.Primary;
        public override Color MenuItemSelected => UiTheme.PrimaryLight;
        public override Color MenuItemSelectedGradientBegin => UiTheme.PrimaryLight;
        public override Color MenuItemSelectedGradientEnd => UiTheme.PrimaryLight;
        public override Color MenuItemPressedGradientBegin => UiTheme.PrimarySoft;
        public override Color MenuItemPressedGradientEnd => UiTheme.PrimarySoft;
    }
}
