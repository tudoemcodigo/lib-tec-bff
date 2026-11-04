namespace TEC.Bff.Sessions;

/// <summary>
/// Armazém dos tokens das sessões do BFF. A implementação padrão (<see cref="InMemoryBffSessionStore"/>) vive na memória de uma
/// instância; com mais de uma instância, registre uma implementação compartilhada (e cifrada em repouso) antes de <c>AddTecBff</c>.
/// </summary>
/// <remarks>Os identificadores de sessão são segredos: implementações não os registram em log nem em exceção.</remarks>
public interface IBffSessionStore
{
    /// <summary>Guarda uma sessão nova. <c>false</c> se o armazém está cheio ou o identificador já existe (falha fechada).</summary>
    ValueTask<bool> TryAddAsync(string sessionId, BffTokenSet tokens, CancellationToken cancellationToken);

    /// <summary>Lê a sessão; <c>null</c> se não existe ou já passou do fim absoluto.</summary>
    ValueTask<BffTokenSet?> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Troca os tokens de uma sessão que ainda existe. <c>false</c> se ela sumiu.</summary>
    ValueTask<bool> TryUpdateAsync(string sessionId, BffTokenSet tokens, CancellationToken cancellationToken);

    /// <summary>Apaga a sessão (idempotente).</summary>
    ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken);
}
