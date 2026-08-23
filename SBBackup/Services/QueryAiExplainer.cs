using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SBBackup.Services;

/// <summary>
/// Explica en lenguaje natural qué hace un script SQL usando una API compatible con
/// OpenAI Chat Completions (Groq). La configuración (endpoint, modelo y API key) se lee
/// de los appsettings.json / appsettings.local.json (junto al .exe y en %LocalAppData%\ST2),
/// para no dejar credenciales en el código fuente.
/// </summary>
public sealed class QueryAiExplainer : IDisposable
{
    private readonly AiSettings _settings;
    private readonly HttpClient _http;

    public QueryAiExplainer()
    {
        _settings = AiSettings.Load();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 15, 120)) };
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Endpoint)
        && !string.IsNullOrWhiteSpace(_settings.Model)
        && !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public async Task<string> ExplicarAsync(
        string sql,
        string? database,
        string? schemaContext = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "La explicación con IA no está configurada. Falta la API key en appsettings.local.json.");
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("No hay ninguna query para explicar.");

        var user =
            $"Base donde se aplica: {(string.IsNullOrWhiteSpace(database) ? "(no indicada)" : database.Trim())}\n\n" +
            "Explicá qué hace este script de forma clara, profesional y sin jerga técnica:\n\n" +
            sql.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint.Trim());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        var payload = new
        {
            model = _settings.Model.Trim(),
            temperature = 0.25,
            messages = new[]
            {
                new { role = "system", content = BuildSystemPrompt(schemaContext) },
                new { role = "user", content = user }
            }
        };
        request.Content = JsonContent.Create(payload);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractError(body, response.StatusCode));

        var text = ExtractContent(body);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("La IA no devolvió texto.");

        return text.Trim();
    }

    /// <summary>
    /// Explica un error/warning capturado en la traza SQL. Usa el glosario de bases genéricas
    /// (<see cref="AiSettings.DatabaseGlossary"/>) como contexto de negocio, pero sin inventar
    /// nada que no esté ahí ni particularidades del cliente.
    /// </summary>
    public async Task<string> ExplicarErrorAsync(
        string category,
        string eventName,
        int? severity,
        int? errorNumber,
        string? database,
        string? message,
        string? sqlText,
        string? schemaContext = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "La explicación con IA no está configurada. Falta la API key en appsettings.local.json.");

        var user = new StringBuilder();
        user.AppendLine("Analizá este evento de error/warning de SQL Server.");
        user.AppendLine($"Categoría: {category}");
        user.AppendLine($"Evento: {eventName}");
        if (severity is not null)
            user.AppendLine($"Severidad: {severity}");
        if (errorNumber is not null)
            user.AppendLine($"Número de error: {errorNumber}");
        user.AppendLine($"Base: {(string.IsNullOrWhiteSpace(database) ? "(no indicada)" : database)}");
        user.AppendLine();
        user.AppendLine("Mensaje:");
        user.AppendLine(string.IsNullOrWhiteSpace(message) ? "(sin mensaje)" : message.Trim());
        user.AppendLine();
        user.AppendLine("T-SQL asociado (si hay):");
        user.AppendLine(string.IsNullOrWhiteSpace(sqlText) ? "(no disponible)" : sqlText.Trim());

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint.Trim());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        var payload = new
        {
            model = _settings.Model.Trim(),
            temperature = 0.2,
            messages = new[]
            {
                new { role = "system", content = BuildErrorSystemPrompt(schemaContext) },
                new { role = "user", content = user.ToString() }
            }
        };
        request.Content = JsonContent.Create(payload);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractError(body, response.StatusCode));

        var text = ExtractContent(body);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("La IA no devolvió texto.");

        return text.Trim();
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Notas manuales opcionales (vacío por defecto; se puede completar en appsettings.local.json,
    /// clave RedaccionIa.GlosarioBases). El contexto principal ya no es esto: es el esquema real
    /// leído en el momento por <see cref="SchemaContextBuilder"/>.
    /// </summary>
    public string DatabaseGlossary => _settings.DatabaseGlossary;

    private string BuildSystemPrompt(string? schemaContext)
    {
        var prompt = SystemPrompt;
        if (!string.IsNullOrWhiteSpace(schemaContext))
            prompt += "\n\nESQUEMA REAL (leído ahora mismo de la base conectada; es la fuente de verdad,\n" +
                      "no la base de otro cliente ni un ejemplo genérico):\n" + schemaContext.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.DatabaseGlossary))
            prompt += "\n\nNotas adicionales del negocio:\n" + _settings.DatabaseGlossary.Trim();
        return prompt;
    }

    private string BuildErrorSystemPrompt(string? schemaContext)
    {
        var prompt = ErrorSystemPrompt;
        if (!string.IsNullOrWhiteSpace(schemaContext))
            prompt += "\n\nESQUEMA REAL (leído ahora mismo de la base conectada; es la fuente de verdad,\n" +
                      "no la base de otro cliente ni un ejemplo genérico):\n" + schemaContext.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.DatabaseGlossary))
            prompt += "\n\nNotas adicionales del negocio:\n" + _settings.DatabaseGlossary.Trim();
        return prompt;
    }

    private const string SystemPrompt =
        """
        Sos un asistente de soporte técnico que explica scripts SQL a personal laboral
        (operadores, mesa de ayuda). Español de Argentina, tono serio, claro y profesional.
        No seas condescendiente ni uses un estilo infantil.

        Objetivo: que alguien sin conocimientos de programación entienda el efecto del script
        antes de ejecutarlo, con lenguaje cotidiano pero respetuoso.

        REGLAS:
        - Evitá jerga de programación (SELECT, DELETE, JOIN, WHERE, COMMIT, schema, etc.).
          Preferí: "consulta un listado", "borra registros", "modifica datos", "crea un usuario
          con permisos elevados", "revisa la integridad de la base".
        - No copies el código. Describí el efecto concreto sobre el sistema o los datos.
        - Sé conciso y concreto. No inventes efectos que el script no tenga.
        - Tono laboral: directo, útil, sin bromas ni frases del estilo "en criollo" o "tranqui".
        - Si te paso el ESQUEMA REAL de la base (más abajo), usalo como fuente de verdad para
          nombrar tablas/columnas en criollo (ej. "la tabla de talonarios"). Si NO te lo paso,
          no inventes nombres de tablas ni columnas: quedate en el efecto general del script.

        Respondé SIEMPRE en texto plano (sin markdown, sin asteriscos, sin ```).
        Usá EXACTAMENTE estas cuatro secciones, cada una en una línea nueva y con una línea
        en blanco entre sí:

        Qué hace:
        (2 a 3 frases claras sobre el efecto real)

        Sobre qué actúa:
        (datos o áreas afectadas en lenguaje simple, o "—")

        Tipo de acción:
        (elegí UNA: Solo lectura / Modifica o borra datos / Mantenimiento de la base)

        Precauciones:
        (riesgos relevantes en tono profesional, o "Sin riesgos evidentes")

        Si el script está incompleto o no se entiende, indicalo con claridad.
        """;

    private const string ErrorSystemPrompt =
        """
        Sos un asistente de soporte SQL Server. Explicás errores y warnings capturados en una traza.
        Español de Argentina, tono laboral, claro y profesional.

        Si te paso el ESQUEMA REAL de la base involucrada (leído en el momento desde el propio
        servidor del cliente, más abajo), usalo como fuente de verdad para nombrar las tablas o
        columnas relevantes. Si NO te lo paso, no inventes tablas, columnas ni causas específicas
        del negocio del cliente: quedate en el significado del error a nivel SQL Server.

        Respondé en texto plano (sin markdown, sin asteriscos, sin ```).
        Usá EXACTAMENTE estas secciones, con una línea en blanco entre sí:

        Qué significa:
        (2 a 3 frases: qué quiere decir el error a nivel SQL Server)

        Causa típica:
        (causas habituales de ese número/mensaje de error, en general)

        Qué revisar:
        (pasos concretos y cortos para diagnosticar, sin asumir nombres de tablas del cliente)

        Si el mensaje es insuficiente, decilo y pedí más contexto (texto SQL, objeto, etc.).
        """;

    private static string ExtractContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return "";
        var message = choices[0].GetProperty("message");
        return message.GetProperty("content").GetString() ?? "";
    }

    private static string ExtractError(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                return code switch
                {
                    "insufficient_quota" => "El proveedor de IA no tiene crédito disponible.",
                    "invalid_api_key" => "La API key de IA no es válida.",
                    "rate_limit_exceeded" => "Demasiadas solicitudes seguidas. Esperá unos segundos e intentá de nuevo.",
                    _ => $"IA ({(int)status}): {msg ?? code ?? "error desconocido"}"
                };
            }
        }
        catch
        {
            // cae al genérico
        }

        var snippet = body.Length > 200 ? body[..200] + "…" : body;
        return $"IA ({(int)status}): {snippet}";
    }

    /// <summary>
    /// Configuración de IA. Endpoint y modelo tienen defaults públicos; la API key
    /// se lee de appsettings.local.json (junto al .exe o en %LocalAppData%\ST2).
    /// </summary>
    private sealed class AiSettings
    {
        public string Endpoint { get; set; } = "https://api.groq.com/openai/v1/chat/completions";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "llama-3.3-70b-versatile";
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Notas manuales OPCIONALES (vacío por defecto a propósito: cada instalación de Bejerman
        /// tiene sus propias tablas, así que no tiene sentido "adivinar" un glosario genérico).
        /// El contexto real se lee en vivo de la base conectada, ver <see cref="SchemaContextBuilder"/>.
        /// Si igual querés agregar alguna aclaración de negocio puntual, se puede completar en
        /// appsettings.local.json (clave RedaccionIa.GlosarioBases) sin recompilar la app.
        /// </summary>
        public string DatabaseGlossary { get; set; } = "";

        public static AiSettings Load()
        {
            var settings = new AiSettings();

            foreach (var path in CandidatePaths())
                Apply(settings, path);

            return settings;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            var baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "appsettings.json");
            yield return Path.Combine(baseDir, "appsettings.local.json");

            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ST2", "appsettings.local.json");
            yield return appData;
        }

        private static void Apply(AiSettings settings, string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("RedaccionIa", out var ia)
                    && !TryGetPropertyCaseInsensitive(doc.RootElement, "RedaccionIa", out ia))
                    return;

                if (TryGetString(ia, "Endpoint", out var endpoint) && endpoint.Length > 0)
                    settings.Endpoint = endpoint;
                if (TryGetString(ia, "ApiKey", out var key) && key.Length > 0 && !IsPlaceholder(key))
                    settings.ApiKey = key;
                if (TryGetString(ia, "Model", out var model) && model.Length > 0)
                    settings.Model = model;
                if (ia.TryGetProperty("TimeoutSeconds", out var t) && t.TryGetInt32(out var secs) && secs > 0)
                    settings.TimeoutSeconds = secs;
                if (TryGetString(ia, "GlosarioBases", out var glosario) && glosario.Length > 0)
                    settings.DatabaseGlossary = glosario;
            }
            catch
            {
                // archivo inválido: se ignora y se usan los valores previos/por defecto
            }
        }

        private static bool IsPlaceholder(string key)
        {
            var k = key.Trim();
            return k.Contains("PEGAR", StringComparison.OrdinalIgnoreCase)
                   || k.Contains("TU_CLAVE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.ValueKind != JsonValueKind.Object)
                return false;
            if (!obj.TryGetProperty(name, out var el) && !TryGetPropertyCaseInsensitive(obj, name, out el))
                return false;
            if (el.ValueKind != JsonValueKind.String)
                return false;
            value = el.GetString() ?? "";
            return true;
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
            return false;
        }
    }
}
