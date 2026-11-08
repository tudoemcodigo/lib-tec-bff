using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Sessions;
using TEC.Bff.Tokens;
using TEC.Security.AspNetCore;

namespace TEC.Bff.Login;

/// <summary>
/// <c>POST /bff/login/password</c>: a tela da SPA manda usuário e senha, o BFF troca no IdP (<c>grant_type=password</c>, client
/// confidencial) e abre a mesma sessão do fluxo de código (ADR SEC 0002 da Doutrina).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>CSRF: exige o cabeçalho anti-CSRF e mesma origem, como as chamadas de API.</item>
/// <item>Resposta única (<c>BFF_CREDENCIAIS_INVALIDAS</c>) para usuário inexistente, senha errada e conta bloqueada.</item>
/// <item>Limite por usuário e por endereço antes de chamar o IdP (429, sem chamar o IdP).</item>
/// <item>A senha não vai a log, métrica, exceção nem resposta; o corpo tem teto de 8 KiB.</item>
/// <item>Os tokens ficam no armazém do servidor antes do cookie; se o cookie não sai, a sessão é apagada.</item>
/// </list>
/// </remarks>
internal sealed class BffPasswordLogin(
    IOptions<BffOptions> options,
    OidcRefreshClient tokens,
    BffLoginThrottle throttle,
    IBffSessionStore store,
    TimeProvider time,
    ILogger<BffPasswordLogin> logger)
{
    private const int MaxBodyBytes = 8 * 1024;
    private const int MaxUsernameLength = 256;
    private const int MaxPasswordLength = 1024;

    public async Task HandleAsync(HttpContext context)
    {
        var bff = options.Value;
        var request = context.Request;
        if (!bff.PasswordLogin.Enabled)
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status404NotFound, BffResponses.NotFound, "Recurso não encontrado.").ConfigureAwait(false);
            return;
        }

        if (!CsrfGuard.HasHeader(request, bff.CsrfHeaderName) || !CsrfGuard.NotCrossSite(request))
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status403Forbidden, BffResponses.Csrf, "Requisição recusada.").ConfigureAwait(false);
            return;
        }

        if (request.ContentType is not { } contentType || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, BffResponses.UnsupportedMediaType, "Envie JSON.").ConfigureAwait(false);
            return;
        }

        var body = await ReadAsync(request, context.RequestAborted).ConfigureAwait(false);
        if (body is null || !IsAcceptable(body.Username, MaxUsernameLength) || !IsAcceptable(body.Password, MaxPasswordLength))
        {
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status400BadRequest, BffResponses.InvalidRequest, "Informe usuário e senha.").ConfigureAwait(false);
            return;
        }

        string username = body.Username!.Trim();
        string address = context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
        if (!throttle.TryAcquire(username, address, bff.PasswordLogin))
        {
            BffLog.PasswordLoginRefused(logger, "tentativas demais");
            BffDiagnostics.Login("tentativas_demais");
            context.Response.Headers.RetryAfter = ((int)bff.PasswordLogin.Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, BffResponses.TooManyAttempts,
                "Muitas tentativas. Espere alguns minutos e tente de novo.").ConfigureAwait(false);
            return;
        }

        var grant = await tokens.PasswordGrantAsync(username, body.Password!, bff.PasswordLogin.Timeout, context.RequestAborted).ConfigureAwait(false);
        if (grant.Kind == PasswordGrantKind.Rejected)
        {
            await WriteInvalidAsync(context, "credencial recusada pelo IdP").ConfigureAwait(false);
            return;
        }

        if (grant.Kind == PasswordGrantKind.Unavailable)
        {
            BffLog.PasswordLoginRefused(logger, "IdP indisponível (" + grant.Failure + ")");
            BffDiagnostics.Login("idp_indisponivel");
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, BffResponses.Unavailable,
                "Serviço temporariamente indisponível.").ConfigureAwait(false);
            return;
        }

        var now = time.GetUtcNow();
        var set = new BffTokenSet(grant.AccessToken!, grant.RefreshToken, now.AddSeconds(grant.ExpiresInSeconds), now.Add(bff.SessionLifetime));
        string sessionId = SessionIds.New();
        if (!await store.TryAddAsync(sessionId, set, context.RequestAborted).ConfigureAwait(false))
        {
            BffLog.PasswordLoginRefused(logger, "armazém de sessões cheio");
            BffDiagnostics.Login("armazem_cheio");
            await BffResponses.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, BffResponses.Unavailable,
                "Serviço temporariamente indisponível.").ConfigureAwait(false);
            return;
        }

        var properties = new AuthenticationProperties();
        properties.Items[BffRuntime.SessionKey] = sessionId;
        var signIn = context.RequestServices.GetRequiredService<WebLoginSignIn>();
        var result = await signIn.SignInWithIdTokenAsync(context, grant.IdToken!, properties, context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            // O TEC.Security recusou a identidade (tenant inativo, token fora do esperado): nada fica no armazém
            await store.RemoveAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            await WriteInvalidAsync(context, "identidade recusada pelo TEC.Security").ConfigureAwait(false);
            return;
        }

        throttle.Succeeded(username);
        BffLog.PasswordSessionCreated(logger);
        BffDiagnostics.Login("ok");
        await BffResponses.WriteJsonAsync(context, new PasswordLoginResponse(CsrfGuard.SafeReturnUrl(body.ReturnUrl))).ConfigureAwait(false);
    }

    private async Task WriteInvalidAsync(HttpContext context, string reason)
    {
        BffLog.PasswordLoginRefused(logger, reason);
        BffDiagnostics.Login("credencial_invalida");
        await BffResponses.WriteErrorAsync(context, StatusCodes.Status401Unauthorized, BffResponses.InvalidCredentials,
            "Usuário ou senha inválidos.").ConfigureAwait(false);
    }

    private static bool IsAcceptable(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static async Task<PasswordLoginRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
            return null;

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.ToArray(),
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<PasswordLoginRequest>)BffResponses.JsonOptions.GetTypeInfo(typeof(PasswordLoginRequest)));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
