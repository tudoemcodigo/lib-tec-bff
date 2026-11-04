using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;

namespace TEC.Bff.Spa;

/// <summary>
/// Serve o build da SPA: <c>index.html</c> com a CSP estrita e o nonce novo a cada resposta, arquivos estáticos com cache correto e
/// fallback de rotas do cliente. Só entrega arquivo de dentro da pasta do build, com tipo conhecido; <c>.map</c>, arquivo oculto,
/// navegação de diretório e caminhos de <c>/bff</c> e <c>/api</c> nunca caem no fallback.
/// </summary>
internal sealed class BffSpaHandler : IDisposable
{
    private const int MaxIndexBytes = 2 * 1024 * 1024;

    private readonly BffOptions _options;
    private readonly PhysicalFileProvider _files;
    private readonly CspPolicy _csp;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();
    private readonly ILogger<BffSpaHandler> _logger;
    private volatile IndexSnapshot? _index;

    public BffSpaHandler(IOptions<BffOptions> options, ILogger<BffSpaHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
        _files = new PhysicalFileProvider(Path.GetFullPath(_options.SpaRootPath!));
        _csp = new CspPolicy(_options.Csp);
        _contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        // Source map nunca sai pelo BFF (FRT-OPE-08): vai só ao sistema de erros
        _contentTypes.Mappings.Remove(".map");
    }

    public void Dispose() => _files.Dispose();

    public async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, BffResponses.MethodNotAllowed, "Método não permitido.").ConfigureAwait(false);
            return;
        }

        string path = request.Path.Value ?? "/";
        if (IsReserved(path) || !IsSafe(path))
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        string lastSegment = path[(path.LastIndexOf('/') + 1)..];
        if (lastSegment.Length == 0 || lastSegment.Equals("index.html", StringComparison.OrdinalIgnoreCase) || !lastSegment.Contains('.', StringComparison.Ordinal))
        {
            await ServeIndexAsync(context).ConfigureAwait(false);
            return;
        }

        var file = _files.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory || file.PhysicalPath is null || !_contentTypes.TryGetContentType(file.Name, out string? contentType)
            || contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        response.ContentType = contentType;
        response.Headers.CacheControl = IsImmutable(path) ? "public, max-age=31536000, immutable" : "no-cache";
        response.ContentLength = file.Length;
        if (HttpMethods.IsHead(request.Method))
            return;

        await response.SendFileAsync(file, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ServeIndexAsync(HttpContext context)
    {
        var snapshot = LoadIndex();
        if (snapshot is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        string nonce = NewNonce();
        byte[] body = Encoding.UTF8.GetBytes(snapshot.Html.Replace(_options.NoncePlaceholder, nonce, StringComparison.Ordinal));

        var response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers[_csp.HeaderName] = _csp.Build(nonce);
        response.Headers["Reporting-Endpoints"] = _csp.ReportingEndpoints;
        response.ContentLength = body.Length;
        if (HttpMethods.IsHead(context.Request.Method))
            return;

        await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Lê o index.html de novo só quando o arquivo muda (data ou tamanho).</summary>
    private IndexSnapshot? LoadIndex()
    {
        var info = _files.GetFileInfo("index.html");
        if (!info.Exists || info.IsDirectory || info.Length > MaxIndexBytes)
            return null;

        var cached = _index;
        if (cached is not null && cached.LastModified == info.LastModified && cached.Length == info.Length)
            return cached;

        string html;
        using (var stream = info.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            html = reader.ReadToEnd();

        if (!html.Contains(_options.NoncePlaceholder, StringComparison.Ordinal))
            BffLog.NonceMissing(_logger, _options.NoncePlaceholder);

        var fresh = new IndexSnapshot(info.LastModified, info.Length, html);
        _index = fresh;
        return fresh;
    }

    private bool IsImmutable(string path) => _options.ImmutablePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsReserved(string path) =>
        path.Equals(BffOptions.BasePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(BffOptions.BasePath + "/", StringComparison.OrdinalIgnoreCase)
        || path.Equals(BffOptions.ApiPath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(BffOptions.ApiPath + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafe(string path)
    {
        if (path.Length > 2048 || path.Any(c => char.IsControl(c) || c == '\\'))
            return false;

        foreach (string segment in path.Split('/'))
        {
            if (segment is "." or ".." || (segment.Length > 0 && segment[0] == '.'))
                return false;
        }

        return true;
    }

    private static string NewNonce()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }

    private static Task NotFoundAsync(HttpContext context) =>
        BffResponses.WriteErrorAsync(context, StatusCodes.Status404NotFound, BffResponses.NotFound, "Recurso não encontrado.");

    private sealed record IndexSnapshot(DateTimeOffset LastModified, long Length, string Html);
}
