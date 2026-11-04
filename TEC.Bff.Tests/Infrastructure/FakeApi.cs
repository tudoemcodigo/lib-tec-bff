using System.Net;
using System.Text;

namespace TEC.Bff.Tests.Infrastructure;

/// <summary>Chamada recebida pela API de mentira.</summary>
internal sealed record ApiCall(string Method, string PathAndQuery, Dictionary<string, string[]> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? string.Join(",", v) : null;
}

/// <summary>API de destino do proxy: grava o que recebeu e responde como o teste configurar.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly List<ApiCall> _calls = [];

    public IReadOnlyList<ApiCall> Calls
    {
        get
        {
            lock (_calls)
                return [.. _calls];
        }
    }

    /// <summary>Resposta padrão: 200 com ETag, <c>Set-Cookie</c> e <c>Server</c> (que o BFF deve descartar).</summary>
    public Func<ApiCall, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "evil=1; Path=/");
        response.Headers.TryAddWithoutValidation("Server", "fake-api");
        return Task.FromResult(response);
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
            headers[name] = [.. values];
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
                headers[name] = [.. values];
        }

        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var call = new ApiCall(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body);
        lock (_calls)
            _calls.Add(call);

        return await Respond(call, cancellationToken).ConfigureAwait(false);
    }
}
