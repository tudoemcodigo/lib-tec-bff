namespace TEC.Bff.Tokens;

/// <summary>Desfecho da troca de usuário e senha no IdP. O <c>ToString()</c> não revela os tokens.</summary>
internal sealed class PasswordGrantResult
{
    private PasswordGrantResult(PasswordGrantKind kind, string? accessToken, string? refreshToken, string? idToken, int expiresInSeconds, string? failure)
    {
        Kind = kind;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        IdToken = idToken;
        ExpiresInSeconds = expiresInSeconds;
        Failure = failure;
    }

    public PasswordGrantKind Kind { get; }

    public string? AccessToken { get; }

    public string? RefreshToken { get; }

    public string? IdToken { get; }

    public int ExpiresInSeconds { get; }

    /// <summary>Só o tipo da falha (nunca mensagem do provedor nem dado do usuário).</summary>
    public string? Failure { get; }

    public static PasswordGrantResult Granted(string accessToken, string? refreshToken, string idToken, int expiresInSeconds) =>
        new(PasswordGrantKind.Granted, accessToken, refreshToken, idToken, expiresInSeconds, null);

    public static PasswordGrantResult Rejected() => new(PasswordGrantKind.Rejected, null, null, null, 0, null);

    public static PasswordGrantResult Unavailable(string failure) => new(PasswordGrantKind.Unavailable, null, null, null, 0, failure);

    public override string ToString() => $"PasswordGrantResult {{ {Kind} }}";
}

/// <summary>Resultado da troca de usuário e senha.</summary>
internal enum PasswordGrantKind
{
    /// <summary>Credencial aceita: tokens recebidos.</summary>
    Granted,

    /// <summary>Credencial recusada (usuário, senha ou conta), sem distinguir o motivo.</summary>
    Rejected,

    /// <summary>IdP fora, lento ou resposta inválida.</summary>
    Unavailable
}
