using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Vault.Abstractions;

namespace TEC.Bff.Tests.Infrastructure;

/// <summary>Logger que guarda as mensagens formatadas (para conferir que token, sessão e segredo não vão para o log).</summary>
internal sealed class TestLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, int EventId, string Message)> Entries { get; } = new();

    public string All => string.Join('\n', Entries.Select(e => e.Message));

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(TestLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((logLevel, eventId.Id, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
    }
}

/// <summary>Relógio manual (começa na hora real e só anda quando o teste manda).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
}

/// <summary>Cofre de mentira: devolve o valor atual e conta as leituras (a rotação vale sem reiniciar).</summary>
internal sealed class FakeSecretReader(string? value) : ISecretReader
{
    public string? Value { get; set; } = value;

    public int Reads;

    public string ProviderName => "Fake";

    public Task<Result<TEC.Vault.Secrets.VaultSecret>> GetSecretAsync(string name, string? version = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult(Value is { } secret
            ? Result.Success(new TEC.Vault.Secrets.VaultSecret(new TEC.Vault.Secrets.SecretProperties { Name = name, Version = "1", Enabled = true }, secret))
            : Result.Failure<TEC.Vault.Secrets.VaultSecret>(Error.NotFound("VAULT_ITEM_NAO_ENCONTRADO", "Segredo não encontrado.")));
    }

    public Task<Result<bool>> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(Value is not null));

    public Task<Result<IReadOnlyList<TEC.Vault.Secrets.SecretProperties>>> ListSecretsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<IReadOnlyList<TEC.Vault.Secrets.SecretProperties>>> ListSecretVersionsAsync(string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
