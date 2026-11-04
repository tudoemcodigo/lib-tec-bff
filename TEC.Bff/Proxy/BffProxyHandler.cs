using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Tokens;
using Yarp.ReverseProxy.Forwarder;

namespace TEC.Bff.Proxy;

/// <summary>
/// Encaminha <c>/api/**</c> à API anexando o bearer da sessão. Fecha por padrão: sem sessão válida não há chamada; o navegador
/// nunca fornece credencial à API (cookie, <c>Authorization</c> e chaves de API são descartados por lista de permissão); só caminhos
/// dentro de <c>/api/</c>, sem segmentos de navegação; corpo, tempo e método limitados.
/// </summary>
internal sealed class BffProxyHandler(
    BffRuntime runtime,
    BffSessionResolver sessions,
    BffTokenBroker tokens,
    IHttpForwarder forwarder,
    IHttpMessageHandlerFactory handlerFactory,
    IOptions<BffOptions> options,
    ILogger<BffProxyHandler> logger)
{
    public const string HttpClientName = "TEC.Bff.Api";

    private readonly BffOptions _options = options.Value;
    private readonly string _destination = options.Value.ApiBaseAddress!.GetLeftPart(UriPartial.Authority);
    private readonly HashSet<string> _allowedHeaders = new(options.Value.ForwardedRequestHeaders, StringComparer.OrdinalIgnoreCase);
    private readonly ForwarderRequestConfig _requestConfig = new() { ActivityTimeout = options.Value.ProxyTimeout };

    public async Task HandleAsync(HttpContext context)
    {
        using var activity = BffDiagnostics.ActivitySource.StartActivity("TEC.Bff.Proxy");
        var request = context.Request;

        if (!PathGuard.IsSafeApiPath(request))
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status400BadRequest, BffResponses.InvalidRequest, "Requisição inválida.").ConfigureAwait(false);
            return;
        }

        var session = await sessions.ResolveAsync(context).ConfigureAwait(false);
        if (session is null)
        {
            BffDiagnostics.Proxy("nao_autenticado");
            await BffResponses.WriteUnauthenticatedAsync(context).ConfigureAwait(false);
            return;
        }

        if (CsrfGuard.ChangesState(request.Method)
            && (!CsrfGuard.HasHeader(request, _options.CsrfHeaderName) || !CsrfGuard.NotCrossSite(request)))
        {
            BffLog.CsrfRefused(logger, request.Method);
            BffDiagnostics.Proxy("csrf");
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status403Forbidden, BffResponses.Csrf, "Requisição recusada.").ConfigureAwait(false);
            return;
        }

        if (request.ContentLength > _options.MaxRequestBodyBytes)
        {
            await RefuseBodyAsync(context).ConfigureAwait(false);
            return;
        }

        var token = await tokens.GetAccessTokenAsync(session.Id, context.RequestAborted).ConfigureAwait(false);
        if (token.Outcome != TokenOutcome.Ok)
        {
            BffDiagnostics.Proxy(token.Outcome == TokenOutcome.NoSession ? "nao_autenticado" : "token_indisponivel");
            if (token.Outcome == TokenOutcome.NoSession)
            {
                await context.SignOutAsync(runtime.CookieScheme).ConfigureAwait(false);
                await BffResponses.WriteUnauthenticatedAsync(context).ConfigureAwait(false);
            }
            else
            {
                context.Response.Headers.RetryAfter = "5";
                await BffResponses.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, BffResponses.Unavailable,
                    "Serviço temporariamente indisponível.").ConfigureAwait(false);
            }

            return;
        }

        LimitBody(context, out var limited);

        // O YARP exige HttpMessageInvoker (não HttpClient, cujo Timeout interferiria no tempo limite do encaminhador); o handler vem da
        // fábrica, com ciclo de vida e reciclagem de conexões cuidados por ela, e por isso não é descartado aqui
        using var invoker = new HttpMessageInvoker(handlerFactory.CreateHandler(HttpClientName), disposeHandler: false);
        var error = await forwarder.SendAsync(context, _destination, invoker, _requestConfig,
            new BffForwardTransformer(token.AccessToken!, _allowedHeaders), context.RequestAborted).ConfigureAwait(false);

        if (error == ForwarderError.None)
        {
            BffDiagnostics.Proxy("ok");
            return;
        }

        if (limited.Exceeded)
        {
            await RefuseBodyAsync(context).ConfigureAwait(false);
            return;
        }

        // Só o tipo do erro: o detalhe (URL, cabeçalho) fica fora de log e de resposta
        BffLog.ForwardFailed(logger, error.ToString());
        BffDiagnostics.Proxy("erro_upstream");
        int status = context.Response.StatusCode >= 400 ? context.Response.StatusCode : StatusCodes.Status502BadGateway;
        await BffResponses.WriteErrorAsync(context, status, BffResponses.Upstream, "Falha ao acessar o serviço.").ConfigureAwait(false);
    }

    private Task RefuseBodyAsync(HttpContext context)
    {
        BffDiagnostics.Proxy("corpo_grande");
        return BffResponses.WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, BffResponses.BodyTooLarge, "Corpo da requisição grande demais.");
    }

    /// <summary>Teto de bytes também para corpo sem <c>Content-Length</c> (chunked), que o pré-teste não enxerga.</summary>
    private void LimitBody(HttpContext context, out LimitedReadStream limited)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = _options.MaxRequestBodyBytes;

        limited = new LimitedReadStream(context.Request.Body, _options.MaxRequestBodyBytes);
        context.Request.Body = limited;
    }

    private sealed class BffForwardTransformer(string accessToken, HashSet<string> allowedHeaders) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);

            // Lista de permissão: o que não foi autorizado a atravessar (Cookie, Authorization, X-Api-Key, X-Forwarded-*...) cai aqui
            Prune(proxyRequest.Headers, allowedHeaders);
            if (proxyRequest.Content is { } content)
                Prune(content.Headers, allowedHeaders);

            proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            bool result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);

            // A API não define cookie no navegador nem revela o servidor
            httpContext.Response.Headers.Remove("Set-Cookie");
            httpContext.Response.Headers.Remove("Server");
            httpContext.Response.Headers.Remove("X-Powered-By");
            return result;
        }

        private static void Prune(HttpHeaders headers, HashSet<string> allowed)
        {
            foreach (string name in headers.Select(h => h.Key).ToList())
            {
                if (!allowed.Contains(name))
                    headers.Remove(name);
            }
        }
    }
}

/// <summary>Caminho seguro para encaminhar: dentro de <c>/api/</c>, sem navegação de diretório nem separadores disfarçados.</summary>
internal static class PathGuard
{
    public static bool IsSafeApiPath(HttpRequest request)
    {
        string path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.Ordinal) && !path.Equals("/api", StringComparison.Ordinal))
            return false;

        if (path.Length > 2048 || path.Any(c => char.IsControl(c) || c == '\\'))
            return false;

        foreach (string segment in path.Split('/'))
        {
            if (segment is "." or "..")
                return false;
        }

        // A forma ainda codificada: %2e, %2f e %5c não têm uso legítimo em rota de API e abrem caminho para sair do prefixo
        string raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget ?? string.Empty;
        return !(raw.Contains("%2e", StringComparison.OrdinalIgnoreCase) || raw.Contains("%2f", StringComparison.OrdinalIgnoreCase)
                 || raw.Contains("%5c", StringComparison.OrdinalIgnoreCase) || raw.Contains("%00", StringComparison.Ordinal));
    }
}

/// <summary>Stream que lê no máximo <c>limit</c> bytes e depois falha com 413.</summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _read;

    public bool Exceeded { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Account(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Account(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Account(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Account(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Account(int read)
    {
        _read += read;
        if (_read > limit)
        {
            Exceeded = true;
            throw new BadHttpRequestException("Corpo da requisição grande demais.", StatusCodes.Status413PayloadTooLarge);
        }

        return read;
    }
}
