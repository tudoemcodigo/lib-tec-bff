using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TEC.Bff.Sessions;
using TEC.Security.Claims;

namespace TEC.Bff.Internal;

/// <summary>Esquemas do login web do TEC.Security usados pelo BFF (descobertos no registro).</summary>
internal sealed class BffRuntime(string cookieScheme)
{
    /// <summary>Chave, nas propriedades do cookie, do identificador da sessão no servidor (o cookie é cifrado e HttpOnly; não leva token).</summary>
    public const string SessionKey = ".tec.bff.sid";

    public string CookieScheme { get; } = cookieScheme;

    public string OidcScheme => CookieScheme + ".oidc";
}

/// <summary>Sessão autenticada: cookie válido (revalidado pelo TEC.Security) e entrada correspondente no armazém do servidor.</summary>
internal sealed class BffSession(string id, SecurityUser user)
{
    public string Id { get; } = id;

    public SecurityUser User { get; } = user;

    public override string ToString() => "BffSession { redigido }";
}

/// <summary>Descobre a sessão de uma requisição. Qualquer divergência (sem cookie, cookie sem sessão, sessão sumida) é "não autenticado".</summary>
internal sealed class BffSessionResolver(BffRuntime runtime, IBffSessionStore store, ILogger<BffSessionResolver> logger)
{
    public async ValueTask<BffSession?> ResolveAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(runtime.CookieScheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is null || result.Properties is null)
            return null;

        var user = new SecurityUser(result.Principal);
        if (!user.IsAuthenticated || user.Kind != TEC.Core.Security.PrincipalKind.User)
            return null;

        if (!result.Properties.Items.TryGetValue(BffRuntime.SessionKey, out string? sessionId) || !SessionIds.IsWellFormed(sessionId)
            || await store.GetAsync(sessionId!, context.RequestAborted).ConfigureAwait(false) is null)
        {
            // Cookie de outro login, de antes de um reinício ou vencido no servidor: limpa para o navegador não insistir nele
            BffLog.SessionMissing(logger);
            await context.SignOutAsync(runtime.CookieScheme).ConfigureAwait(false);
            return null;
        }

        return new BffSession(sessionId!, user);
    }
}
