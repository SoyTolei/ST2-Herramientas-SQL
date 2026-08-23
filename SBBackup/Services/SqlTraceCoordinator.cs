using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>
/// Opciones de captura alineadas a categorías tipo Profiler / Express Profiler.
/// </summary>
public sealed class SqlTraceOptions
{
    public bool ErrorsAndWarnings { get; init; } = true;
    public bool SecurityAudit { get; init; }
    public bool Sessions { get; init; }
    public bool Tsql { get; init; } = true;

    public bool AnySelected =>
        ErrorsAndWarnings || SecurityAudit || Sessions || Tsql;
}

/// <summary>
/// Traza con Extended Events (equivalente moderno a Profiler):
/// Errors and Warnings, Security Audit, Sessions, Stored Procedures, TSQL.
/// </summary>
public sealed class SqlTraceCoordinator : IDisposable
{
    public const string SessionName = "ST2_SqlTrace";

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "msdb", "tempdb", "mssqlsystemresource"
    };

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _databaseNamesById = new();
    private bool _running;
    private bool _paused;
    private bool _ownedSession;
    private SqlTraceOptions _options = new();

    public bool IsRunning => _running;
    public bool IsPaused => _paused;
    public bool HasActiveSession => _ownedSession;

    private sealed record XeCapabilities(
        bool DatabaseName,
        bool DatabaseId,
        bool SqlText,
        bool ClientAppName,
        bool ClientHostname,
        bool SessionId,
        bool Username);

    public static bool IsSystemDatabase(string? databaseName) =>
        !string.IsNullOrWhiteSpace(databaseName) && SystemDatabases.Contains(databaseName.Trim());

    public async Task StartAsync(
        string masterConnectionString,
        SqlTraceOptions options,
        CancellationToken ct)
    {
        if (_running)
            return;
        if (!options.AnySelected)
            throw new InvalidOperationException("Marcá al menos una categoría de eventos.");

        _options = options;

        await using var conn = OpenMaster(masterConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureStoppedAsync(conn, ct).ConfigureAwait(false);
        await DropIfExistsAsync(conn, ct).ConfigureAwait(false);

        var caps = await DetectCapabilitiesAsync(conn, ct).ConfigureAwait(false);
        await RefreshDatabaseIdMapAsync(conn, ct).ConfigureAwait(false);

        // 1) completo  2) sin eventos opcionales  3) sin sql_text (a veces inválido por evento)
        Exception? last = null;
        foreach (var (optional, useSqlText) in new[]
                 {
                     (true, true),
                     (false, true),
                     (false, false)
                 })
        {
            try
            {
                await DropIfExistsAsync(conn, ct).ConfigureAwait(false);
                await CreateSessionAsync(conn, options, caps, optional, useSqlText, ct).ConfigureAwait(false);
                last = null;
                break;
            }
            catch (SqlException ex)
            {
                last = ex;
            }
        }

        if (last is not null)
            throw last;

        await StartSessionAsync(conn, ct).ConfigureAwait(false);

        _seen.Clear();
        _ownedSession = true;
        _paused = false;
        _running = true;
    }

    /// <summary>
    /// Pausa la captura (STATE = STOP) sin eliminar la sesión Extended Events.
    /// </summary>
    public async Task PauseAsync(string masterConnectionString, CancellationToken ct)
    {
        if (!_ownedSession || _paused)
            return;

        await using var conn = OpenMaster(masterConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await EnsureStoppedAsync(conn, ct).ConfigureAwait(false);
        _running = false;
        _paused = true;
    }

    /// <summary>
    /// Reanuda una sesión pausada (STATE = START).
    /// </summary>
    public async Task ResumeAsync(string masterConnectionString, CancellationToken ct)
    {
        if (!_ownedSession || !_paused)
            return;

        await using var conn = OpenMaster(masterConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await StartSessionAsync(conn, ct).ConfigureAwait(false);
        _paused = false;
        _running = true;
    }

    public async Task StopAsync(string masterConnectionString, CancellationToken ct)
    {
        if (!_running && !_ownedSession)
            return;

        try
        {
            await using var conn = OpenMaster(masterConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await EnsureStoppedAsync(conn, ct).ConfigureAwait(false);
            await DropIfExistsAsync(conn, ct).ConfigureAwait(false);
        }
        finally
        {
            _running = false;
            _paused = false;
            _ownedSession = false;
            _seen.Clear();
            _databaseNamesById.Clear();
        }
    }

    public async Task<IReadOnlyList<SqlTraceEvent>> PollAsync(
        string masterConnectionString,
        CancellationToken ct)
    {
        if (!_running)
            return Array.Empty<SqlTraceEvent>();

        await using var conn = OpenMaster(masterConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (_databaseNamesById.Count == 0)
            await RefreshDatabaseIdMapAsync(conn, ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(
            """
            SELECT CAST(t.target_data AS xml)
            FROM sys.dm_xe_session_targets AS t
            INNER JOIN sys.dm_xe_sessions AS s
                ON s.address = t.event_session_address
            WHERE s.name = @name
              AND t.target_name = N'ring_buffer';
            """,
            conn)
        {
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@name", SessionName);

        var xmlObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (xmlObj is null or DBNull)
            return Array.Empty<SqlTraceEvent>();

        var xmlText = Convert.ToString(xmlObj) ?? "";
        if (string.IsNullOrWhiteSpace(xmlText))
            return Array.Empty<SqlTraceEvent>();

        return ParseRingBuffer(xmlText);
    }

    public static string ToCsv(IEnumerable<SqlTraceEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FechaHora;Categoria;Evento;Severidad;Error;DuracionMs;SPID;Base;Login;App;Host;Mensaje;Sql");
        foreach (var e in events)
        {
            sb.Append(Csv(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))).Append(';')
                .Append(Csv(e.Category)).Append(';')
                .Append(Csv(e.EventName)).Append(';')
                .Append(e.Severity?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
                .Append(e.ErrorNumber?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
                .Append(e.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
                .Append(e.SessionId?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
                .Append(Csv(e.DatabaseName)).Append(';')
                .Append(Csv(e.LoginName)).Append(';')
                .Append(Csv(e.ClientApp)).Append(';')
                .Append(Csv(e.ClientHost)).Append(';')
                .Append(Csv(e.Message)).Append(';')
                .Append(Csv(e.SqlText))
                .AppendLine();
        }

        return sb.ToString();
    }

    public static string ToTxt(IEnumerable<SqlTraceEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ST2 · Traza SQL");
        sb.AppendLine("Generado: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('-', 72));
        foreach (var e in events)
        {
            sb.AppendLine($"[{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {e.Category} · {e.EventName}");
            if (e.Severity is not null || e.ErrorNumber is not null)
                sb.AppendLine($"  Severidad: {e.Severity}  Error: {e.ErrorNumber}");
            if (e.DurationMs is not null)
                sb.AppendLine($"  Duración: {e.DurationMs} ms");
            sb.AppendLine($"  SPID: {e.SessionId}  Base: {e.DatabaseName}  Login: {e.LoginName}");
            sb.AppendLine($"  App: {e.ClientApp}  Host: {e.ClientHost}");
            if (!string.IsNullOrWhiteSpace(e.Message))
                sb.AppendLine("  Mensaje: " + e.Message);
            if (!string.IsNullOrWhiteSpace(e.SqlText))
                sb.AppendLine("  SQL: " + e.SqlText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public void Dispose()
    {
    }

    private List<SqlTraceEvent> ParseRingBuffer(string xmlText)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlText, LoadOptions.None);
        }
        catch
        {
            return [];
        }

        var list = new List<SqlTraceEvent>();

        foreach (var node in doc.Descendants("event"))
        {
            try
            {
                var name = (string?)node.Attribute("name") ?? "";
                var tsRaw = (string?)node.Attribute("timestamp");
                if (!DateTime.TryParse(
                        tsRaw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var ts))
                    ts = DateTime.Now;

                var data = node.Elements("data")
                    .ToDictionary(
                        d => (string?)d.Attribute("name") ?? "",
                        d => FirstValue(d),
                        StringComparer.OrdinalIgnoreCase);

                var actions = node.Elements("action")
                    .ToDictionary(
                        a => (string?)a.Attribute("name") ?? "",
                        a => FirstValue(a),
                        StringComparer.OrdinalIgnoreCase);

                string GetData(string key) =>
                    data.TryGetValue(key, out var v) ? v : "";
                string GetAction(string key) =>
                    actions.TryGetValue(key, out var v) ? v : "";

                var category = Categorize(name);
                int? severity = TryInt(GetData("severity"));
                int? errorNumber = TryInt(GetData("error_number"));
                long? durationMs = null;
                var durationRaw = TryLong(GetData("duration"));
                if (durationRaw is long micros)
                    durationMs = Math.Max(0, micros / 1000);

                var sqlText = FirstNonEmpty(
                    GetAction("sql_text"),
                    GetData("statement"),
                    GetData("batch_text"),
                    GetData("object_name"));

                var message = name is "error_reported" or "attention" or "login_failed"
                    ? FirstNonEmpty(GetData("message"), GetData("user_defined"), GetData("error_message"))
                    : "";

                var evt = new SqlTraceEvent
                {
                    Timestamp = ts.ToLocalTime(),
                    Category = category,
                    EventName = name,
                    Severity = severity,
                    ErrorNumber = errorNumber,
                    DurationMs = durationMs,
                    SessionId = TryInt(GetAction("session_id")),
                    DatabaseName = ResolveDatabaseName(GetAction("database_name"), GetData("database_name"), GetAction("database_id"), GetData("database_id")),
                    LoginName = FirstNonEmpty(GetAction("username"), GetAction("server_principal_name")),
                    ClientApp = GetAction("client_app_name"),
                    ClientHost = GetAction("client_hostname"),
                    Message = message,
                    SqlText = CollapseWhitespace(sqlText)
                };

                // Errores/warnings siempre se muestran (aunque la sesión esté en master).
                // En TSQL sí ocultamos ruido de bases de sistema.
                var isErrorEvent = name is "error_reported" or "attention" or "login_failed";
                if (!isErrorEvent && IsSystemDatabase(evt.DatabaseName))
                {
                    _seen.Add(evt.Key);
                    continue;
                }

                if (_seen.Add(evt.Key))
                    list.Add(evt);
            }
            catch
            {
                // Un evento malformado no debe frenar el resto del buffer.
            }
        }

        list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return list;
    }

    private string Categorize(string eventName) =>
        eventName switch
        {
            "error_reported" or "attention" => "Errors and Warnings",
            "login_failed" => "Security Audit",
            "login" or "logout" =>
                _options.Sessions && !_options.SecurityAudit ? "Sessions" : "Security Audit",
            "sql_batch_completed" or "sql_statement_completed" => "TSQL",
            _ => "Evento"
        };

    private static async Task CreateSessionAsync(
        SqlConnection conn,
        SqlTraceOptions options,
        XeCapabilities caps,
        bool includeOptionalEvents,
        bool includeSqlText,
        CancellationToken ct)
    {
        var events = new List<string>();
        var actions = BuildActionsClause(caps, includeSqlText);
        var notSystemDb = await BuildNotSystemDatabasePredicateAsync(conn, caps, ct).ConfigureAwait(false);

        if (options.ErrorsAndWarnings)
        {
            // Sin filtro de base: muchos errores vienen con database_name = master o NULL
            // y el predicado <> master los descartaba por completo.
            events.Add(
                $"""
                ADD EVENT sqlserver.error_reported(
                    {actions}
                    WHERE ([severity] >= (10)))
                """);
            if (includeOptionalEvents)
            {
                events.Add(
                    $"""
                    ADD EVENT sqlserver.attention(
                        {actions})
                    """);
            }
        }

        if (options.SecurityAudit || options.Sessions)
        {
            events.Add(
                $"""
                ADD EVENT sqlserver.login(
                    {actions})
                """);
            events.Add(
                $"""
                ADD EVENT sqlserver.logout(
                    {actions})
                """);
        }

        if (options.SecurityAudit && includeOptionalEvents)
        {
            events.Add(
                $"""
                ADD EVENT sqlserver.login_failed(
                    {actions})
                """);
        }

        if (options.Tsql)
        {
            // Solo movimientos: excluye master/model/msdb/tempdb cuando el predicado está disponible.
            events.Add(
                string.IsNullOrEmpty(notSystemDb)
                    ? $"""
                    ADD EVENT sqlserver.sql_batch_completed(
                        {actions})
                    """
                    : $"""
                    ADD EVENT sqlserver.sql_batch_completed(
                        {actions}
                        WHERE ({notSystemDb}))
                    """);
        }

        if (events.Count == 0)
            throw new InvalidOperationException("No hay eventos para capturar.");

        var sql =
            "CREATE EVENT SESSION [ST2_SqlTrace] ON SERVER\r\n" +
            string.Join(",\r\n", events) +
            """

            ADD TARGET package0.ring_buffer(SET max_memory = (8192))
            WITH (
                MAX_MEMORY = 16 MB,
                EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
                MAX_DISPATCH_LATENCY = 2 SECONDS,
                STARTUP_STATE = OFF);
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildActionsClause(XeCapabilities caps, bool includeSqlText)
    {
        var parts = new List<string>();
        if (caps.ClientAppName)
            parts.Add("sqlserver.client_app_name");
        if (caps.ClientHostname)
            parts.Add("sqlserver.client_hostname");
        // Preferimos nombre; en SQL viejo solo existe database_id.
        if (caps.DatabaseName)
            parts.Add("sqlserver.database_name");
        else if (caps.DatabaseId)
            parts.Add("sqlserver.database_id");
        if (caps.SessionId)
            parts.Add("sqlserver.session_id");
        if (includeSqlText && caps.SqlText)
            parts.Add("sqlserver.sql_text");
        if (caps.Username)
            parts.Add("sqlserver.username");

        if (parts.Count == 0)
            return "";

        return "ACTION(\r\n                " + string.Join(",\r\n                ", parts) + ")\r\n            ";
    }

    private static async Task<XeCapabilities> DetectCapabilitiesAsync(SqlConnection conn, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(
                         """
                         SELECT name
                         FROM sys.dm_xe_objects
                         WHERE object_type = N'action'
                           AND name IN (
                               N'database_name', N'database_id', N'sql_text',
                               N'client_app_name', N'client_hostname',
                               N'session_id', N'username');
                         """,
                         conn)
                     { CommandTimeout = 15 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                names.Add(reader.GetString(0));
        }

        // Si dm_xe_objects no devolvió nada (permiso raro), asumimos el set moderno
        // y el fallback de CreateSession cubre SQL antiguos.
        if (names.Count == 0)
        {
            return new XeCapabilities(
                DatabaseName: true,
                DatabaseId: true,
                SqlText: true,
                ClientAppName: true,
                ClientHostname: true,
                SessionId: true,
                Username: true);
        }

        return new XeCapabilities(
            DatabaseName: names.Contains("database_name"),
            DatabaseId: names.Contains("database_id"),
            SqlText: names.Contains("sql_text"),
            ClientAppName: names.Contains("client_app_name"),
            ClientHostname: names.Contains("client_hostname"),
            SessionId: names.Contains("session_id"),
            Username: names.Contains("username"));
    }

    /// <summary>
    /// Predicado XE para TSQL: excluye bases de sistema.
    /// El lenguaje de predicados de Extended Events NO es T-SQL.
    /// En SQL antiguos sin action database_name usamos database_id.
    /// </summary>
    private static async Task<string> BuildNotSystemDatabasePredicateAsync(
        SqlConnection conn,
        XeCapabilities caps,
        CancellationToken ct)
    {
        if (caps.DatabaseName)
        {
            return
                "[sqlserver].[database_name] <> N'master' AND " +
                "[sqlserver].[database_name] <> N'model' AND " +
                "[sqlserver].[database_name] <> N'msdb' AND " +
                "[sqlserver].[database_name] <> N'tempdb'";
        }

        if (!caps.DatabaseId)
            return "";

        var ids = new List<int>();
        await using var cmd = new SqlCommand(
            """
            SELECT database_id
            FROM sys.databases
            WHERE name IN (N'master', N'model', N'msdb', N'tempdb');
            """,
            conn)
        { CommandTimeout = 15 };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetInt32(0));

        if (ids.Count == 0)
            return "";

        return string.Join(" AND ", ids.Select(id => $"[sqlserver].[database_id] <> ({id})"));
    }

    private async Task RefreshDatabaseIdMapAsync(SqlConnection conn, CancellationToken ct)
    {
        _databaseNamesById.Clear();
        await using var cmd = new SqlCommand(
            "SELECT database_id, name FROM sys.databases;",
            conn)
        { CommandTimeout = 15 };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            _databaseNamesById[reader.GetInt32(0)] = reader.GetString(1);
    }

    private string ResolveDatabaseName(params string[] candidates)
    {
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var s = raw.Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                && _databaseNamesById.TryGetValue(id, out var name))
                return name;

            // Evitar mostrar solo un número si no pudimos mapear.
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                continue;

            return s;
        }

        return "";
    }

    private static async Task StartSessionAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "ALTER EVENT SESSION [ST2_SqlTrace] ON SERVER STATE = START;",
            conn)
        { CommandTimeout = 30 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureStoppedAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!await SessionExistsAsync(conn, ct).ConfigureAwait(false))
            return;

        try
        {
            await using var cmd = new SqlCommand(
                "ALTER EVENT SESSION [ST2_SqlTrace] ON SERVER STATE = STOP;",
                conn)
            { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException)
        {
        }
    }

    private static async Task DropIfExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!await SessionExistsAsync(conn, ct).ConfigureAwait(false))
            return;

        await using var cmd = new SqlCommand(
            "DROP EVENT SESSION [ST2_SqlTrace] ON SERVER;",
            conn)
        { CommandTimeout = 30 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> SessionExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT 1 FROM sys.server_event_sessions WHERE name = @name;",
            conn)
        { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@name", SessionName);
        var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return o is not null and not DBNull;
    }

    private static SqlConnection OpenMaster(string masterConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = "master"
        };
        return new SqlConnection(builder.ConnectionString);
    }

    private static string FirstValue(XElement el)
    {
        var value = el.Element("value")?.Value;
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        var text = el.Element("text")?.Value;
        return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
    }

    private static int? TryInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? TryLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return "";
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (ch is '\n' or '\t' or ' ')
            {
                if (!space)
                {
                    sb.Append(' ');
                    space = true;
                }
            }
            else
            {
                sb.Append(ch);
                space = false;
            }
        }

        return sb.ToString().Trim();
    }

    private static string Csv(string? value)
    {
        var v = (value ?? "").Replace("\"", "\"\"");
        if (v.Contains(';') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v + "\"";
        return v;
    }
}
