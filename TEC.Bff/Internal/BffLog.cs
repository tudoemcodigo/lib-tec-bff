using Microsoft.Extensions.Logging;

namespace TEC.Bff.Internal;

/// <summary>
/// Mensagens de log do BFF (eventos 6001-6014, faixa própria do componente). Segurança: token, refresh token, identificador de sessão, cookie, corpo, URL com
/// identificador e mensagem de exceção do provedor nunca são registrados; só desfecho, tipo da exceção e nomes de configuração.
/// </summary>
internal static partial class BffLog
{
    [LoggerMessage(6001, LogLevel.Information, "BFF: login concluído, sessão criada no servidor.")]
    public static partial void SessionCreated(ILogger logger);

    [LoggerMessage(6002, LogLevel.Warning, "BFF: login recusado ({Reason}).")]
    public static partial void LoginRefused(ILogger logger, string reason);

    [LoggerMessage(6003, LogLevel.Information, "BFF: access token renovado.")]
    public static partial void TokenRefreshed(ILogger logger);

    [LoggerMessage(6004, LogLevel.Warning, "BFF: renovação recusada pelo provedor; sessão encerrada.")]
    public static partial void RefreshRejected(ILogger logger);

    [LoggerMessage(6005, LogLevel.Warning, "BFF: renovação indisponível ({FailureType}); a sessão foi mantida.")]
    public static partial void RefreshUnavailable(ILogger logger, string failureType);

    [LoggerMessage(6006, LogLevel.Warning, "BFF: chamada recusada pelo CSRF em {Method}.")]
    public static partial void CsrfRefused(ILogger logger, string method);

    [LoggerMessage(6007, LogLevel.Warning, "BFF: falha ao encaminhar para a API ({Error}).")]
    public static partial void ForwardFailed(ILogger logger, string error);

    [LoggerMessage(6008, LogLevel.Warning, "BFF: o index.html não contém o marcador de nonce '{Placeholder}'; a CSP bloqueará os scripts.")]
    public static partial void NonceMissing(ILogger logger, string placeholder);

    [LoggerMessage(6009, LogLevel.Warning, "BFF: violação de CSP relatada ({Directive}).")]
    public static partial void CspViolation(ILogger logger, string directive);

    [LoggerMessage(6010, LogLevel.Information, "BFF: sessão sem entrada no servidor (reinício ou expiração); novo login exigido.")]
    public static partial void SessionMissing(ILogger logger);

    [LoggerMessage(6011, LogLevel.Warning, "BFF: busca do tema sazonal falhou ({Reason}).")]
    public static partial void ThemeRefreshFailed(ILogger logger, string reason);

    [LoggerMessage(6012, LogLevel.Warning, "BFF: origem do tema indisponível; servindo o último documento válido.")]
    public static partial void ThemeServedStale(ILogger logger);

    [LoggerMessage(6013, LogLevel.Warning, "BFF: login por senha recusado ({Reason}).")]
    public static partial void PasswordLoginRefused(ILogger logger, string reason);

    [LoggerMessage(6014, LogLevel.Information, "BFF: login por senha concluído, sessão criada no servidor.")]
    public static partial void PasswordSessionCreated(ILogger logger);
}
