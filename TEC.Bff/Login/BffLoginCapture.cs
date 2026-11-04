using System.Globalization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Sessions;

namespace TEC.Bff.Login;

/// <summary>
/// Guarda os tokens do login no servidor <b>sem</b> <c>SaveTokens</c> (que o TEC.Security proíbe de propósito: tokens não vão
/// para o cookie). Os eventos do OIDC só leem o que o provedor devolveu: <c>OnTokenValidated</c> separa os tokens na requisição e
/// <c>OnTicketReceived</c>, que só roda se o TEC.Security aceitou e normalizou a identidade, grava no armazém e põe no cookie
/// apenas o identificador aleatório da sessão. Login recusado não deixa nada no armazém.
/// </summary>
internal static class BffLoginCapture
{
    private static readonly object PendingKey = new();

    public static void Register(IServiceCollection services, string oidcScheme) =>
        services.AddOptions<OpenIdConnectOptions>(oidcScheme).PostConfigure<IServiceProvider>((options, _) => Install(options.Events));

    /// <summary>Encadeia os dois eventos preservando os delegates que já existem (por exemplo, o do segredo no cofre).</summary>
    internal static void Install(OpenIdConnectEvents events)
    {
        var previousValidated = events.OnTokenValidated;
        events.OnTokenValidated = async context =>
        {
            await previousValidated(context).ConfigureAwait(false);

            var response = context.TokenEndpointResponse;
            context.HttpContext.Items[PendingKey] = string.IsNullOrEmpty(response?.AccessToken)
                ? null
                : new PendingTokens(response.AccessToken, response.RefreshToken, response.ExpiresIn);
        };

        var previousTicket = events.OnTicketReceived;
        events.OnTicketReceived = async context =>
        {
            await previousTicket(context).ConfigureAwait(false);

            // O TEC.Security já recusou (ou o delegate respondeu): nada a guardar
            if (context.Result is { Handled: true } or { Skipped: true } or { Failure: not null })
                return;

            var services = context.HttpContext.RequestServices;
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TEC.Bff.Login");
            var options = services.GetRequiredService<IOptions<BffOptions>>().Value;
            var time = services.GetRequiredService<TimeProvider>();

            var pending = context.HttpContext.Items.TryGetValue(PendingKey, out object? value) ? value as PendingTokens : null;
            context.HttpContext.Items.Remove(PendingKey);
            if (pending is null)
            {
                BffLog.LoginRefused(logger, "resposta do provedor sem access token");
                BffDiagnostics.Login("sem_token");
                context.HandleResponse();
                await BffResponses.WriteUnauthenticatedAsync(context.HttpContext).ConfigureAwait(false);
                return;
            }

            var now = time.GetUtcNow();
            var tokens = new BffTokenSet(pending.AccessToken, pending.RefreshToken, now.AddSeconds(pending.ExpiresInSeconds), now.Add(options.SessionLifetime));
            string sessionId = SessionIds.New();
            var store = services.GetRequiredService<IBffSessionStore>();
            if (!await store.TryAddAsync(sessionId, tokens, context.HttpContext.RequestAborted).ConfigureAwait(false))
            {
                BffLog.LoginRefused(logger, "armazém de sessões cheio");
                BffDiagnostics.Login("armazem_cheio");
                context.HandleResponse();
                await BffResponses.WriteErrorAsync(context.HttpContext, StatusCodes.Status503ServiceUnavailable, BffResponses.Unavailable,
                    "Serviço temporariamente indisponível.").ConfigureAwait(false);
                return;
            }

            context.Properties!.Items[BffRuntime.SessionKey] = sessionId;
            BffLog.SessionCreated(logger);
            BffDiagnostics.Login("ok");
        };
    }

    /// <summary>Tokens da resposta do provedor, só até o fim da requisição do login.</summary>
    private sealed class PendingTokens(string accessToken, string? refreshToken, string? expiresIn)
    {
        public string AccessToken { get; } = accessToken;

        public string? RefreshToken { get; } = refreshToken;

        /// <summary><c>expires_in</c> limitado a 1 s–24 h; ausente ou inválido vale 60 s (conservador: força renovar logo).</summary>
        public int ExpiresInSeconds { get; } =
            int.TryParse(expiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ? Math.Clamp(seconds, 1, 86_400) : 60;

        public override string ToString() => "PendingTokens { redigido }";
    }
}
