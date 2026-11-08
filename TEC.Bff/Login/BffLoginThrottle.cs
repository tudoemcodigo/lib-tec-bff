using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TEC.Bff.Login;

/// <summary>
/// Conta as tentativas de login por senha por usuário e por endereço, em janela deslizante, na memória da instância. O usuário
/// entra só como hash (o nome digitado nunca fica guardado), e o total de chaves tem teto: cheio, recusa (falha fechada).
/// </summary>
internal sealed class BffLoginThrottle(TimeProvider time)
{
    private const int MaxKeys = 100_000;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);

    /// <summary>Registra a tentativa se as duas chaves ainda estão dentro do limite; senão, recusa sem registrar.</summary>
    public bool TryAcquire(string username, string address, BffPasswordLoginOptions options)
    {
        var now = time.GetUtcNow();
        var userKey = "u:" + Hash(username);
        var addressKey = "a:" + address;
        if (_attempts.Count >= MaxKeys)
            Prune(now, options.Window);
        if (_attempts.Count >= MaxKeys)
            return false;

        var user = _attempts.GetOrAdd(userKey, _ => new Queue<DateTimeOffset>());
        var ip = _attempts.GetOrAdd(addressKey, _ => new Queue<DateTimeOffset>());
        // Ordem fixa das travas (usuário, depois endereço): sem deadlock entre tentativas concorrentes
        lock (user)
        {
            lock (ip)
            {
                Expire(user, now, options.Window);
                Expire(ip, now, options.Window);
                if (user.Count >= options.MaxAttemptsPerUser || ip.Count >= options.MaxAttemptsPerAddress)
                    return false;

                user.Enqueue(now);
                ip.Enqueue(now);
                return true;
            }
        }
    }

    /// <summary>Login aceito: zera a contagem do usuário (a do endereço continua, contra varredura de usuários).</summary>
    public void Succeeded(string username) => _attempts.TryRemove("u:" + Hash(username), out _);

    private void Prune(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, queue) in _attempts)
        {
            lock (queue)
            {
                Expire(queue, now, window);
                if (queue.Count == 0)
                    _attempts.TryRemove(key, out _);
            }
        }
    }

    private static void Expire(Queue<DateTimeOffset> queue, DateTimeOffset now, TimeSpan window)
    {
        while (queue.Count > 0 && now - queue.Peek() >= window)
            queue.Dequeue();
    }

    private static string Hash(string username) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToUpperInvariant())));
}
