using System.Diagnostics;

namespace TEC.Bff.Sessions;

/// <summary>
/// Tokens de uma sessão, guardados só no servidor. Nunca chegam ao navegador, a log, a exceção, a métrica nem a <c>ToString()</c>.
/// </summary>
[DebuggerDisplay("BffTokenSet (redigido)")]
public sealed class BffTokenSet
{
    /// <summary>Cria o conjunto de tokens.</summary>
    /// <exception cref="ArgumentException">Access token vazio.</exception>
    public BffTokenSet(string accessToken, string? refreshToken, DateTimeOffset accessTokenExpiresAt, DateTimeOffset sessionExpiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        AccessToken = accessToken;
        RefreshToken = string.IsNullOrEmpty(refreshToken) ? null : refreshToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        SessionExpiresAt = sessionExpiresAt;
    }

    /// <summary>Access token enviado à API como bearer.</summary>
    public string AccessToken { get; }

    /// <summary>Refresh token (ausente: a sessão acaba quando o access token vence).</summary>
    public string? RefreshToken { get; }

    /// <summary>Vencimento do access token.</summary>
    public DateTimeOffset AccessTokenExpiresAt { get; }

    /// <summary>Fim absoluto da sessão no servidor.</summary>
    public DateTimeOffset SessionExpiresAt { get; }

    /// <summary>Cópia com tokens novos (renovação) e o mesmo fim absoluto de sessão.</summary>
    public BffTokenSet Renewed(string accessToken, string? refreshToken, DateTimeOffset accessTokenExpiresAt) =>
        new(accessToken, refreshToken ?? RefreshToken, accessTokenExpiresAt, SessionExpiresAt);

    /// <inheritdoc />
    public override string ToString() => "BffTokenSet { redigido }";
}
