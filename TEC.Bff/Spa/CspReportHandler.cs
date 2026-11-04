using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Bff.Internal;

namespace TEC.Bff.Spa;

/// <summary>
/// Recebe os relatórios de violação da CSP (<c>report-uri</c> e <c>report-to</c>). Só conta e registra a diretiva violada (de uma
/// lista fechada, para não criar rótulos sem limite); o conteúdo do relatório, que pode trazer URLs e trechos de código da página,
/// nunca é registrado. Corpo, tipo e profundidade do JSON têm teto.
/// </summary>
internal static class CspReportHandler
{
    private const int MaxBytes = 16 * 1024;
    private const int MaxReports = 20;

    private static readonly string[] AllowedTypes = ["application/csp-report", "application/reports+json", "application/json"];

    private static readonly HashSet<string> KnownDirectives = new(StringComparer.Ordinal)
    {
        "default-src", "script-src", "script-src-elem", "script-src-attr", "style-src", "style-src-elem", "style-src-attr", "img-src",
        "font-src", "connect-src", "object-src", "base-uri", "frame-ancestors", "form-action", "frame-src", "media-src", "worker-src",
        "manifest-src", "child-src"
    };

    public static async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        string? mediaType = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (mediaType is null || !AllowedTypes.Contains(mediaType))
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, BffResponses.UnsupportedMediaType, "Tipo de conteúdo não suportado.").ConfigureAwait(false);
            return;
        }

        byte[]? body = await ReadBoundedAsync(request, context.RequestAborted).ConfigureAwait(false);
        if (body is null)
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, BffResponses.BodyTooLarge, "Corpo da requisição grande demais.").ConfigureAwait(false);
            return;
        }

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TEC.Bff.Csp");
        foreach (string directive in ExtractDirectives(body))
        {
            BffDiagnostics.Csp(directive);
            BffLog.CspViolation(logger, directive);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBytes)
            return null;

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[2048];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Diretivas violadas, de relatório legado (<c>csp-report</c>) ou da Reporting API (lista de <c>csp-violation</c>). JSON inválido não gera nada.</summary>
    private static List<string> ExtractDirectives(byte[] body)
    {
        var found = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray().Take(MaxReports))
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("body", out var reportBody))
                        Add(found, Read(reportBody, "effectiveDirective") ?? Read(reportBody, "violatedDirective"));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("csp-report", out var legacy))
            {
                Add(found, Read(legacy, "effective-directive") ?? Read(legacy, "violated-directive"));
            }
        }
        catch (JsonException)
        {
            // Relatório malformado: ignorado, sem registrar o conteúdo
        }

        return found;
    }

    private static string? Read(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Add(List<string> found, string? directive)
    {
        string name = directive?.Split(' ', 2)[0] ?? string.Empty;
        found.Add(KnownDirectives.Contains(name) ? name : "outra");
    }
}
