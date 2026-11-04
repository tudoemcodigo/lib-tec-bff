using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Sessions;

namespace TEC.Bff.Tokens;

/// <summary>Desfecho de pedir o access token de uma sessão.</summary>
internal enum TokenOutcome
{
    /// <summary>Token válido disponível.</summary>
    Ok,

    /// <summary>Sessão inexistente, vencida ou encerrada pelo provedor: novo login.</summary>
    NoSession,

    /// <summary>Token vencido e o provedor não respondeu: tente de novo mais tarde (a sessão continua).</summary>
    Unavailable
}

/// <summary>Resultado de <see cref="BffTokenBroker.GetAccessTokenAsync"/>. O <c>ToString()</c> não revela o token.</summary>
internal readonly struct TokenResult(TokenOutcome outcome, string? accessToken = null)
{
    public TokenOutcome Outcome { get; } = outcome;

    public string? AccessToken { get; } = accessToken;

    public override string ToString() => $"TokenResult {{ {Outcome} }}";
}

/// <summary>
/// Entrega o access token de uma sessão, renovando-o quando falta pouco para vencer. A renovação é <b>single-flight</b> por
/// sessão: chamadas concorrentes esperam a mesma renovação (um refresh token rotativo não sobrevive a dois usos).
/// </summary>
internal sealed class BffTokenBroker(
    IBffSessionStore store,
    OidcRefreshClient refreshClient,
    IOptions<BffOptions> options,
    TimeProvider time,
    ILogger<BffTokenBroker> logger)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<TokenResult>>> _inFlight = new(StringComparer.Ordinal);

    public async Task<TokenResult> GetAccessTokenAsync(string sessionId, CancellationToken cancellationToken)
    {
        var tokens = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (tokens is null)
            return new TokenResult(TokenOutcome.NoSession);

        if (!NeedsRefresh(tokens))
            return new TokenResult(TokenOutcome.Ok, tokens.AccessToken);

        // A renovação roda desacoplada da requisição que a pediu: se essa requisição for cancelada, as outras que esperam o mesmo
        // resultado não podem falhar por isso (o próprio pedido ao provedor tem tempo limite)
        var flight = _inFlight.GetOrAdd(sessionId, id => new Lazy<Task<TokenResult>>(() => RefreshAsync(id)));
        return await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool NeedsRefresh(BffTokenSet tokens) => tokens.AccessTokenExpiresAt - time.GetUtcNow() <= options.Value.RefreshSkew;

    private async Task<TokenResult> RefreshAsync(string sessionId)
    {
        try
        {
            // Quem chegou atrasado pode encontrar a renovação de outra rodada já feita
            var tokens = await store.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (tokens is null)
                return new TokenResult(TokenOutcome.NoSession);
            if (!NeedsRefresh(tokens))
                return new TokenResult(TokenOutcome.Ok, tokens.AccessToken);

            if (tokens.RefreshToken is null)
                return await StillValidAsync(sessionId, tokens).ConfigureAwait(false);

            var result = await refreshClient.RefreshAsync(tokens.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            switch (result.Kind)
            {
                case RefreshKind.Renewed:
                    var renewed = tokens.Renewed(result.AccessToken!, result.RefreshToken, time.GetUtcNow().AddSeconds(result.ExpiresInSeconds));
                    if (!await store.TryUpdateAsync(sessionId, renewed, CancellationToken.None).ConfigureAwait(false))
                        return new TokenResult(TokenOutcome.NoSession);

                    BffLog.TokenRefreshed(logger);
                    BffDiagnostics.Refresh("renovado");
                    return new TokenResult(TokenOutcome.Ok, renewed.AccessToken);

                case RefreshKind.Rejected:
                    await store.RemoveAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    BffLog.RefreshRejected(logger);
                    BffDiagnostics.Refresh("recusado");
                    return new TokenResult(TokenOutcome.NoSession);

                default:
                    BffLog.RefreshUnavailable(logger, result.Failure ?? "desconhecida");
                    BffDiagnostics.Refresh("indisponivel");
                    // Provedor fora: o token atual ainda serve enquanto não vencer; vencido, é indisponível (sem apagar a sessão)
                    return tokens.AccessTokenExpiresAt > time.GetUtcNow()
                        ? new TokenResult(TokenOutcome.Ok, tokens.AccessToken)
                        : new TokenResult(TokenOutcome.Unavailable);
            }
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
        }
    }

    /// <summary>Sem refresh token: serve o token até vencer; vencido, a sessão acaba.</summary>
    private async Task<TokenResult> StillValidAsync(string sessionId, BffTokenSet tokens)
    {
        if (tokens.AccessTokenExpiresAt > time.GetUtcNow())
            return new TokenResult(TokenOutcome.Ok, tokens.AccessToken);

        await store.RemoveAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        return new TokenResult(TokenOutcome.NoSession);
    }
}
