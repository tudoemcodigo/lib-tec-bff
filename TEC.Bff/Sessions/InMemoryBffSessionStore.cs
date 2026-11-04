using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace TEC.Bff.Sessions;

/// <summary>
/// Armazém em memória, thread-safe, com teto de sessões. Vale para <b>uma instância</b>: reiniciar o processo apaga as sessões
/// (o usuário entra de novo) e uma segunda instância não enxerga as sessões da primeira.
/// </summary>
public sealed class InMemoryBffSessionStore : IBffSessionStore
{
    private readonly ConcurrentDictionary<string, BffTokenSet> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _addLock = new();
    private readonly TimeProvider _time;
    private readonly int _maxSessions;

    /// <summary>Cria o armazém com o teto de <see cref="BffOptions.MaxSessions"/>.</summary>
    public InMemoryBffSessionStore(IOptions<BffOptions> options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxSessions = options.Value.MaxSessions;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Quantidade de sessões guardadas (inclui as vencidas ainda não removidas).</summary>
    public int Count => _sessions.Count;

    /// <inheritdoc />
    public ValueTask<bool> TryAddAsync(string sessionId, BffTokenSet tokens, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(tokens);

        lock (_addLock)
        {
            if (_sessions.Count >= _maxSessions)
                PurgeExpired();
            if (_sessions.Count >= _maxSessions)
                return ValueTask.FromResult(false);

            return ValueTask.FromResult(_sessions.TryAdd(sessionId, tokens));
        }
    }

    /// <inheritdoc />
    public ValueTask<BffTokenSet?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var tokens))
            return ValueTask.FromResult<BffTokenSet?>(null);

        if (tokens.SessionExpiresAt <= _time.GetUtcNow())
        {
            _sessions.TryRemove(new KeyValuePair<string, BffTokenSet>(sessionId, tokens));
            return ValueTask.FromResult<BffTokenSet?>(null);
        }

        return ValueTask.FromResult<BffTokenSet?>(tokens);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryUpdateAsync(string sessionId, BffTokenSet tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        while (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var current))
        {
            if (_sessions.TryUpdate(sessionId, tokens, current))
                return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    private void PurgeExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, value) in _sessions)
        {
            if (value.SessionExpiresAt <= now)
                _sessions.TryRemove(new KeyValuePair<string, BffTokenSet>(key, value));
        }
    }
}
