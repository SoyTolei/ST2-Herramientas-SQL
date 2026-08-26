using System.Globalization;
using SBBackup.Models;
using SBBackup.Services;
using SBBackup.Ui;

namespace SBBackup;

/// <summary>Configura bases, días/hora y registra la tarea en el Programador de Windows.</summary>
internal sealed class ScheduleBackupForm : Form
{
    private static readonly (string Code, string Label)[] WeekDays =
    [
        ("MON", "Lunes"),
        ("TUE", "Martes"),
        ("WED", "Miércoles"),
        ("THU", "Jueves"),
        ("FRI", "Viernes"),
        ("SAT", "Sábado"),
        ("SUN", "Domingo")
    ];

    private readonly string _server;
    private readonly IReadOnlyList<ServerDatabaseRow> _databases;
    private readonly HashSet<string> _preselected;

    private Label _lblIntro = null!;
    private CheckedListBox _lstBases = null!;
    private readonly CheckBox[] _dayChecks = new CheckBox[WeekDays.Length];
    private DateTimePicker _timePicker = null!;
    private Label _lblFolder = null!;
    private Label _lblStatus = null!;
    private Panel _statusPanel = null!;
    private RoundedActionButton _btnSave = null!;
    private RoundedActionButton _btnDisable = null!;
    private RoundedActionButton _btnTest = null!;

    public ScheduleBackupForm(
        string server,
        IReadOnlyList<ServerDatabaseRow> databases,
        IEnumerable<string>? preselectedPhysicalNames)
    {
        _server = server.Trim();
        _databases = databases;
        _preselected = new HashSet<string>(
            (preselectedPhysicalNames ?? []).Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);

        Text = "Programar backups automáticos";
        UiTheme.ApplyDpiAwareScaling(this);
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.AppBack;
        Font = UiTheme.UiFont(9.5f);
        // Tamaños lógicos @ 96 DPI; BindSubFormScreenSizing los adapta al monitor/DPI.
        ClientSize = new Size(620, 740);
        MinimumSize = new Size(540, 560);

        BuildUi();
        LoadExistingProfile();
        RefreshStatus();

        UiTheme.BindSubFormScreenSizing(this, preferredWidth: 620, preferredHeight: 740);
        Shown += (_, _) => RefreshWrapWidths();
        Resize += (_, _) => RefreshWrapWidths();
        DpiChanged += (_, _) => BeginInvoke(RefreshWrapWidths);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16),
            BackColor = UiTheme.AppBack
        };
        // Intro y bloques fijos en AutoSize; listado y estado se llevan el espacio libre.
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));

        _lblIntro = new Label
        {
            Text = "Elegí las bases, los días de la semana y la hora. Windows ejecuta el backup solo: " +
                   "podés cerrar ST2. La PC debe estar encendida y tu sesión iniciada.",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = UiTheme.TextMuted,
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(_lblIntro, 0, 0);

        _lstBases = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Font = UiTheme.UiFont(9.25f),
            Margin = Padding.Empty
        };
        foreach (var db in _databases)
        {
            var label = string.IsNullOrWhiteSpace(db.GridNombre) ||
                        db.GridNombre.Equals(db.PhysicalName, StringComparison.OrdinalIgnoreCase)
                ? db.PhysicalName
                : $"{db.PhysicalName}  ·  {db.GridNombre}";
            var idx = _lstBases.Items.Add(new DbItem(db.PhysicalName, label));
            if (_preselected.Contains(db.PhysicalName))
                _lstBases.SetItemChecked(idx, true);
        }

        _lstBases.DisplayMember = nameof(DbItem.Display);
        root.Controls.Add(
            CardSection.CreateFill("Bases a respaldar (marcá al menos una)", _lstBases),
            0, 1);

        var scheduleInner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.EmpresaPanelBg,
            Padding = new Padding(4, 2, 4, 4),
            Margin = Padding.Empty
        };
        scheduleInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scheduleInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scheduleInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        scheduleInner.Controls.Add(new Label
        {
            Text = "Días (marcá uno o más):",
            AutoSize = true,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);

        var daysRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = UiTheme.EmpresaPanelBg,
            Margin = new Padding(0, 0, 0, 6),
            Padding = Padding.Empty
        };
        for (var i = 0; i < WeekDays.Length; i++)
        {
            var cb = new CheckBox
            {
                Text = WeekDays[i].Label,
                AutoSize = true,
                Tag = WeekDays[i].Code,
                Margin = new Padding(0, 2, 12, 4),
                ForeColor = UiTheme.TextPrimary,
                Font = UiTheme.UiFont(9.25f),
                Checked = i < 5
            };
            _dayChecks[i] = cb;
            daysRow.Controls.Add(cb);
        }
        scheduleInner.Controls.Add(daysRow, 0, 1);

        var timeRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiTheme.EmpresaPanelBg,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 2)
        };
        timeRow.Controls.Add(new Label
        {
            Text = "Hora:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9.25f, FontStyle.Bold),
            Margin = new Padding(0, 6, 10, 0)
        });
        _timePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 96,
            Value = DateTime.Today.AddHours(2),
            Font = UiTheme.UiFont(10f),
            Margin = new Padding(0, 2, 0, 2),
            MinimumSize = new Size(88, 26)
        };
        timeRow.Controls.Add(_timePicker);
        scheduleInner.Controls.Add(timeRow, 0, 2);

        root.Controls.Add(CardSection.Create("Cuándo ejecutarlo", scheduleInner), 0, 2);

        var folderPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = UiTheme.EmpresaPanelBg,
            Padding = new Padding(4, 2, 4, 2),
            Margin = Padding.Empty
        };
        _lblFolder = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = OutputPathHelper.GetScheduledBackupDirectory() +
                   Path.DirectorySeparatorChar +
                   "{día dd-MM-yyyy}",
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(9f)
        };
        folderPanel.Controls.Add(_lblFolder);
        root.Controls.Add(
            CardSection.Create(
                "Carpeta destino (subcarpeta por día · se borra lo que tenga más de 7 días)",
                folderPanel),
            0, 3);

        _statusPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.EmpresaPanelBg,
            Padding = new Padding(8, 6, 8, 6),
            Margin = Padding.Empty,
            AutoScroll = true
        };
        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.UiFont(8.75f),
            BackColor = UiTheme.EmpresaPanelBg
        };
        _statusPanel.Controls.Add(_lblStatus);
        root.Controls.Add(
            CardSection.CreateFill("Estado de la programación", _statusPanel),
            0, 4);

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 0),
            BackColor = UiTheme.AppBack
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

        _btnTest = new RoundedActionButton { Text = "Probar ahora", Dock = DockStyle.Fill };
        UiTheme.StyleInfoButton(_btnTest);
        _btnTest.Margin = new Padding(0, 0, 6, 0);
        _btnTest.MinimumSize = new Size(0, 36);
        _btnTest.Click += async (_, _) => await RunTestAsync().ConfigureAwait(true);

        _btnDisable = new RoundedActionButton { Text = "Quitar programación", Dock = DockStyle.Fill };
        UiTheme.StyleDangerButton(_btnDisable);
        _btnDisable.Margin = new Padding(0, 0, 6, 0);
        _btnDisable.MinimumSize = new Size(0, 36);
        _btnDisable.Click += (_, _) => DisableSchedule();

        _btnSave = new RoundedActionButton { Text = "Guardar y programar", Dock = DockStyle.Fill };
        UiTheme.StyleBackupButton(_btnSave);
        _btnSave.Margin = new Padding(0, 0, 6, 0);
        _btnSave.MinimumSize = new Size(0, 36);
        _btnSave.Click += (_, _) => SaveAndRegister();

        var btnCancel = new Button
        {
            Text = "Cerrar",
            Dock = DockStyle.Fill,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(0),
            MinimumSize = new Size(0, 36)
        };
        UiTheme.StyleSecondaryButton(btnCancel);
        CancelButton = btnCancel;

        buttons.Controls.Add(_btnTest, 0, 0);
        buttons.Controls.Add(_btnDisable, 1, 0);
        buttons.Controls.Add(_btnSave, 2, 0);
        buttons.Controls.Add(btnCancel, 3, 0);
        root.Controls.Add(buttons, 0, 5);

        Controls.Add(root);
    }

    private void RefreshWrapWidths()
    {
        if (!IsHandleCreated)
            return;

        var contentW = Math.Max(200, ClientSize.Width - 48);
        _lblIntro.MaximumSize = new Size(contentW, 0);
        _lblFolder.MaximumSize = new Size(Math.Max(160, contentW - 24), 0);
        _lblStatus.MaximumSize = new Size(Math.Max(160, _statusPanel.ClientSize.Width - 28), 0);
        // Forzar recálculo del AutoSize multilínea.
        _lblIntro.PerformLayout();
        _lblStatus.PerformLayout();
    }

    private void LoadExistingProfile()
    {
        var profile = ScheduledBackupStore.TryLoad();
        if (profile is null)
            return;

        if (TimeSpan.TryParseExact(profile.TimeOfDay?.Trim() ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out var ts))
            _timePicker.Value = DateTime.Today.Add(ts);

        var daySet = new HashSet<string>(profile.DaysOfWeek ?? [], StringComparer.OrdinalIgnoreCase);
        if (daySet.Count == 0
            && string.Equals(profile.Frequency, ScheduledBackupProfile.FrequencyDaily, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var d in WeekDays)
                daySet.Add(d.Code);
        }

        if (daySet.Count > 0)
        {
            foreach (var cb in _dayChecks)
            {
                var code = cb.Tag as string ?? "";
                cb.Checked = daySet.Contains(code);
            }
        }

        if (profile.DatabaseNames.Count > 0)
        {
            for (var i = 0; i < _lstBases.Items.Count; i++)
            {
                if (_lstBases.Items[i] is DbItem item)
                    _lstBases.SetItemChecked(i, profile.DatabaseNames.Contains(item.PhysicalName, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private void RefreshStatus()
    {
        try
        {
            _lblStatus.Text = ScheduledBackupStatus.BuildDetailedSummary();
            _lblStatus.ForeColor = UiTheme.TextPrimary;
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "No se pudo armar el detalle de la programación:\n" +
                              UserMessageSpanish.ShortTechnical(ex);
            _lblStatus.ForeColor = UiTheme.TextMuted;
        }

        RefreshWrapWidths();
    }

    private ScheduledBackupProfile BuildProfileFromUi(bool enabled)
    {
        var names = new List<string>();
        for (var i = 0; i < _lstBases.Items.Count; i++)
        {
            if (!_lstBases.GetItemChecked(i))
                continue;
            if (_lstBases.Items[i] is DbItem item)
                names.Add(item.PhysicalName);
        }

        if (names.Count == 0)
            throw new InvalidOperationException("Marcá al menos una base para el backup programado.");

        var days = new List<string>();
        foreach (var cb in _dayChecks)
        {
            if (cb.Checked && cb.Tag is string code)
                days.Add(code);
        }

        if (days.Count == 0)
            throw new InvalidOperationException("Marcá al menos un día de la semana.");

        var folder = OutputPathHelper.GetScheduledBackupDirectory();
        if (!OutputPathHelper.TryValidateWriteAccess(folder, out var err))
            throw new InvalidOperationException(err);

        return new ScheduledBackupProfile
        {
            Enabled = enabled,
            Server = _server,
            DatabaseNames = names,
            OutputDirectory = folder,
            Frequency = days.Count == 7
                ? ScheduledBackupProfile.FrequencyDaily
                : ScheduledBackupProfile.FrequencyWeekly,
            TimeOfDay = _timePicker.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
            DaysOfWeek = days
        };
    }

    private void SaveAndRegister()
    {
        try
        {
            if (WindowsTaskSchedulerService.IsLikelyDevHost())
            {
                var cont = MessageBox.Show(
                    this,
                    "Estás en modo desarrollo (dotnet). La tarea apuntará a dotnet.exe y probablemente falle al ejecutarse sola.\n\n" +
                    "¿Guardar de todos modos? (Para producción usá el .exe de publish.)",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (cont != DialogResult.Yes)
                    return;
            }

            var profile = BuildProfileFromUi(enabled: true);
            ScheduledBackupStore.Save(profile);
            WindowsTaskSchedulerService.Register(
                profile,
                WindowsTaskSchedulerService.ResolveExecutablePath(),
                ScheduledBackupStore.GetProfilePath());

            RefreshStatus();
            MessageBox.Show(
                this,
                "Backup programado.\n\n" +
                $"Frecuencia: {DescribeSchedule(profile)}\n" +
                $"Destino (por día):\n{profile.OutputDirectory}\\{{día dd-MM-yyyy}}\n\n" +
                "Podés cerrar ST2; Windows lo ejecutará solo.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                UserMessageSpanish.FriendlyError("No se pudo programar el backup.", ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void DisableSchedule()
    {
        var confirm = MessageBox.Show(
            this,
            "¿Quitar la programación?\n\n" +
            "Se elimina la tarea de Windows. Ya no se harán backups automáticos " +
            "hasta que vuelvas a guardar una programación.",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
            return;

        try
        {
            WindowsTaskSchedulerService.Unregister();
            var existing = ScheduledBackupStore.TryLoad();
            if (existing is not null)
            {
                existing.Enabled = false;
                ScheduledBackupStore.Save(existing);
            }

            RefreshStatus();
            MessageBox.Show(
                this,
                "Programación quitada. Los backups ya no se ejecutarán solos.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                UserMessageSpanish.FriendlyError("No se pudo quitar la programación.", ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task RunTestAsync()
    {
        try
        {
            var profile = BuildProfileFromUi(enabled: true);
            ScheduledBackupStore.Save(profile);

            _btnTest.Enabled = false;
            _btnSave.Enabled = false;
            _btnDisable.Enabled = false;
            _lblStatus.Text = "Ejecutando prueba de backup…";

            var code = await ScheduledBackupRunner
                .RunAsync(ScheduledBackupStore.GetProfilePath(), CancellationToken.None)
                .ConfigureAwait(true);

            RefreshStatus();
            var dayDir = OutputPathHelper.GetScheduledBackupDayDirectory();
            if (code == 0)
            {
                MessageBox.Show(
                    this,
                    "Prueba OK.\n\nZIP del día en:\n" + dayDir +
                    "\n\nLog en:\n" + Path.Combine(profile.OutputDirectory, "logs"),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    this,
                    "La prueba terminó con errores. Revisá el log en:\n" +
                    Path.Combine(profile.OutputDirectory, "logs"),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                UserMessageSpanish.FriendlyError("No se pudo ejecutar la prueba.", ex),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _btnTest.Enabled = true;
            _btnSave.Enabled = true;
            _btnDisable.Enabled = true;
            RefreshStatus();
        }
    }

    private static string DescribeSchedule(ScheduledBackupProfile profile)
    {
        var labels = WeekDays
            .Where(d => profile.DaysOfWeek.Contains(d.Code, StringComparer.OrdinalIgnoreCase))
            .Select(d => d.Label)
            .ToList();

        if (labels.Count == 7)
            return $"todos los días a las {profile.TimeOfDay}";

        return $"{string.Join(", ", labels)} a las {profile.TimeOfDay}";
    }

    private sealed class DbItem(string physicalName, string display)
    {
        public string PhysicalName { get; } = physicalName;
        public string Display { get; } = display;
        public override string ToString() => Display;
    }
}
