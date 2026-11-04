using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;

namespace TEC.Bff.Theme;

/// <summary>Asset de tema já validado, pronto para servir.</summary>
internal sealed record ThemeAsset(byte[] Content, string ContentType);

/// <summary>
/// Guarda o documento de temas validado e os assets do tema ativo. Uma busca por vez (single-flight, inclusive na falha, com recuo
/// curto para não martelar a origem), tempo limite e teto de bytes em toda leitura, e <c>stale-if-error</c>: o último documento
/// bom continua valendo por <see cref="BffThemeOptions.StaleMaxAge"/> se a origem cair; documento ruim nunca substitui o bom.
/// </summary>
internal sealed class ThemeStore(IHttpClientFactory httpClients, IOptions<BffOptions> options, TimeProvider clock, ILogger<ThemeStore> logger)
{
    public const string HttpClientName = "TEC.Bff.Theme";

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _documentGate = new(1, 1);
    private readonly SemaphoreSlim _assetGate = new(1, 1);
    private readonly Dictionary<string, (ThemeAsset Asset, DateTimeOffset At)> _assets = new(StringComparer.Ordinal);
    private (ThemesDocument Document, DateTimeOffset At)? _snapshot;
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

    private BffThemeOptions Options => options.Value.Theme;

    /// <summary>Tema ativo hoje, ou <c>null</c> (recurso desligado, sem tema na data, ou origem indisponível sem cache válido).</summary>
    public async Task<SeasonalTheme?> GetActiveThemeAsync(CancellationToken cancellationToken)
    {
        if (Options.Url is null)
            return null;
        var document = await GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ThemeSelector.Select(document, ThemeSelector.Today(clock.GetUtcNow()));
    }

    /// <summary>Asset do tema ATIVO. Qualquer outro id ou arquivo (inclusive de tema fora da janela) é <c>null</c>.</summary>
    public async Task<ThemeAsset?> GetAssetAsync(string id, string file, CancellationToken cancellationToken)
    {
        // Validação antes de qualquer uso: id e arquivo nunca carregam barra, ponto duplo nem nada fora do padrão
        if (Options.Url is not { } documentUrl || !ThemeDocumentParser.IdPattern().IsMatch(id) || !ThemeDocumentParser.AssetPattern().IsMatch(file))
            return null;
        var active = await GetActiveThemeAsync(cancellationToken).ConfigureAwait(false);
        if (active is null || active.Id != id || (active.Banner != file && active.Logo != file))
            return null;

        string key = id + "/" + file;
        await _assetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            _assets.TryGetValue(key, out var cached);
            if (cached.Asset is not null && now - cached.At < Options.CacheTtl)
                return cached.Asset;

            var fetched = await FetchAssetAsync(new Uri(documentUrl, $"assets/{id}/{file}"), file, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
            {
                // Só o tema ativo fica em memória: o conjunto é limitado a banner e logo
                foreach (string stale in _assets.Keys.Where(k => k != key && !IsActiveKey(active, k)).ToList())
                    _assets.Remove(stale);
                _assets[key] = (fetched, now);
                return fetched;
            }

            return cached.Asset is not null && now - cached.At <= Options.StaleMaxAge ? cached.Asset : null;
        }
        finally
        {
            _assetGate.Release();
        }
    }

    private static bool IsActiveKey(SeasonalTheme active, string key) =>
        key == active.Id + "/" + active.Banner || key == active.Id + "/" + active.Logo;

    private async Task<ThemesDocument?> GetDocumentAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (current is { } fresh && clock.GetUtcNow() - fresh.At < Options.CacheTtl)
            return fresh.Document;

        await _documentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            current = _snapshot;
            if (current is { } again && now - again.At < Options.CacheTtl)
                return again.Document;

            if (now - _lastFailure >= FailureBackoff)
            {
                var fetched = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
                if (fetched is not null)
                {
                    _snapshot = (fetched, now);
                    BffDiagnostics.Theme("ok");
                    return fetched;
                }

                _lastFailure = now;
            }

            if (current is { } old && now - old.At <= Options.StaleMaxAge)
            {
                BffDiagnostics.Theme("obsoleto");
                BffLog.ThemeServedStale(logger);
                return old.Document;
            }

            BffDiagnostics.Theme("erro");
            return null;
        }
        finally
        {
            _documentGate.Release();
        }
    }

    private async Task<ThemesDocument?> FetchDocumentAsync(CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadLimitedAsync(Options.Url!, Options.MaxDocumentBytes, "documento", cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;
        if (!ThemeDocumentParser.TryParse(bytes, out var document, out string error))
        {
            BffLog.ThemeRefreshFailed(logger, "invalido: " + error);
            return null;
        }

        return document;
    }

    private async Task<ThemeAsset?> FetchAssetAsync(Uri uri, string file, CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadLimitedAsync(uri, Options.MaxAssetBytes, "asset", cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;

        // O tipo servido vem da extensão validada e o conteúdo precisa ser a imagem declarada; o Content-Type da origem não vale
        string extension = file[(file.LastIndexOf('.') + 1)..];
        if (!ImageSignature.Matches(extension, bytes))
        {
            BffLog.ThemeRefreshFailed(logger, "asset nao e imagem");
            return null;
        }

        return new ThemeAsset(bytes, extension switch { "png" => "image/png", "webp" => "image/webp", _ => "image/avif" });
    }

    /// <summary>Lê uma URL com teto de bytes e de tempo; qualquer desvio devolve <c>null</c> (falha fechada).</summary>
    private async Task<byte[]?> ReadLimitedAsync(Uri uri, long maxBytes, string what, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.FetchTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                BffLog.ThemeRefreshFailed(logger, $"{what}: status {(int)response.StatusCode}");
                return null;
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                BffLog.ThemeRefreshFailed(logger, what + ": grande demais");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    BffLog.ThemeRefreshFailed(logger, what + ": grande demais");
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            BffLog.ThemeRefreshFailed(logger, what + ": tempo esgotado");
            return null;
        }
        catch (HttpRequestException e)
        {
            // Só o tipo: a mensagem pode trazer o endereço resolvido
            BffLog.ThemeRefreshFailed(logger, $"{what}: {e.GetType().Name}");
            return null;
        }
    }
}

/// <summary>Assinatura (magic bytes) das imagens aceitas: o conteúdo precisa ser a imagem que a extensão declara.</summary>
internal static class ImageSignature
{
    public static bool Matches(string extension, ReadOnlySpan<byte> bytes) => extension switch
    {
        "png" => bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        "avif" => bytes.Length >= 12 && bytes[4..12].SequenceEqual("ftypavif"u8),
        _ => false
    };
}
