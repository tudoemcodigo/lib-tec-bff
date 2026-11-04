using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TEC.Bff.Internal;

namespace TEC.Bff.Tokens;

/// <summary>Desfecho de um pedido de renovação ao provedor.</summary>
internal enum RefreshKind
{
    /// <summary>Tokens novos recebidos.</summary>
    Renewed,

    /// <summary>O provedor recusou o refresh token (revogado, vencido, sessão encerrada): a sessão acaba.</summary>
    Rejected,

    /// <summary>Provedor fora, lento ou resposta inválida: nada se conclui; a sessão é mantida.</summary>
    Unavailable
}

/// <summary>Resultado da renovação. O <c>ToString()</c> não revela os tokens.</summary>
internal sealed class RefreshResult
{
    public RefreshResult(RefreshKind kind, string? accessToken = null, string? refreshToken = null, int expiresInSeconds = 0, string? failure = null)
    {
        Kind = kind;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresInSeconds = expiresInSeconds;
        Failure = failure;
    }

    public RefreshKind Kind { get; }

    public string? AccessToken { get; }

    public string? RefreshToken { get; }

    public int ExpiresInSeconds { get; }

    /// <summary>Só o tipo da falha (nunca mensagem de exceção do provedor).</summary>
    public string? Failure { get; }

    public override string ToString() => $"RefreshResult {{ {Kind} }}";
}

/// <summary>
/// Renova o access token (<c>grant_type=refresh_token</c>) no endpoint do provedor descoberto pelo próprio login do TEC.Security.
/// O client secret vem do mesmo caminho do login (cofre TEC.Vault lido a cada uso: a rotação vale sem reiniciar). O <c>HttpClient</c>
/// não segue redirecionamentos (um 307 reenviaria o segredo) nem guarda cookies, e a resposta tem teto de tamanho.
/// </summary>
internal sealed class OidcRefreshClient(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IAuthenticationSchemeProvider schemes,
    IServiceProvider rootServices,
    BffRuntime runtime,
    IOptions<BffOptions> options)
{
    public const string HttpClientName = "TEC.Bff.Refresh";

    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxTokenLength = 16 * 1024;

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var activity = BffDiagnostics.ActivitySource.StartActivity("TEC.Bff.RefreshToken");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.RefreshTimeout);

        try
        {
            var oidc = oidcOptions.Get(runtime.OidcScheme);
            var configuration = oidc.Configuration
                                ?? await (oidc.ConfigurationManager ?? throw new InvalidOperationException("sem metadados do provedor"))
                                    .GetConfigurationAsync(timeout.Token).ConfigureAwait(false);
            if (!IsAcceptableEndpoint(configuration.TokenEndpoint))
                return new RefreshResult(RefreshKind.Unavailable, failure: "endpoint de token inválido");

            string? secret = await ResolveClientSecretAsync(oidc, timeout.Token).ConfigureAwait(false);
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", oidc.ClientId ?? string.Empty)
            };
            if (!string.IsNullOrEmpty(secret))
                form.Add(new("client_secret", secret));

            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            using var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (status is 400 or 401 or 403)
                return new RefreshResult(RefreshKind.Rejected);
            if (!response.IsSuccessStatusCode)
                return new RefreshResult(RefreshKind.Unavailable, failure: "status " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));

            byte[]? body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return body is null ? new RefreshResult(RefreshKind.Unavailable, failure: "resposta grande demais") : Parse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or InvalidOperationException or IOException)
        {
            // Só o tipo: a mensagem do provedor ou da rede pode trazer URL, cabeçalho ou parte do corpo
            return new RefreshResult(RefreshKind.Unavailable, failure: exception.GetType().Name);
        }
    }

    private static RefreshResult Parse(byte[] body)
    {
        TokenEndpointResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, BffJsonContext.Default.TokenEndpointResponse);
        }
        catch (JsonException)
        {
            return new RefreshResult(RefreshKind.Unavailable, failure: "JSON inválido");
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.AccessToken) || parsed.AccessToken.Length > MaxTokenLength
            || (parsed.RefreshToken is { } refresh && refresh.Length > MaxTokenLength)
            || !string.Equals(parsed.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            return new RefreshResult(RefreshKind.Unavailable, failure: "resposta inválida");

        int seconds = parsed.ExpiresIn.ValueKind == JsonValueKind.Number && parsed.ExpiresIn.TryGetInt32(out int value) ? Math.Clamp(value, 1, 86_400) : 60;
        return new RefreshResult(RefreshKind.Renewed, parsed.AccessToken, parsed.RefreshToken, seconds);
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            return null;

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsAcceptableEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// O secret do login: o das opções, ou o que o provedor (TEC.Security.OpenIdConnect) injeta no pedido de troca do código a
    /// partir do cofre. Reusa o evento do login em vez de duplicar o nome do segredo na configuração do BFF.
    /// </summary>
    private async Task<string?> ResolveClientSecretAsync(OpenIdConnectOptions oidc, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
            return oidc.ClientSecret;

        var scheme = await schemes.GetSchemeAsync(runtime.OidcScheme).ConfigureAwait(false);
        if (scheme is null)
            return null;

        var http = new DefaultHttpContext { RequestServices = rootServices, RequestAborted = cancellationToken };
        var context = new AuthorizationCodeReceivedContext(http, scheme, oidc, new AuthenticationProperties())
        {
            TokenEndpointRequest = new OpenIdConnectMessage()
        };
        await oidc.Events.AuthorizationCodeReceived(context).ConfigureAwait(false);
        return context.TokenEndpointRequest.ClientSecret;
    }
}
