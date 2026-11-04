using System.Globalization;
using System.Net;
using System.Text;

namespace TEC.Bff.Tests.Infrastructure;

/// <summary>Navegador mínimo para os testes: guarda cookies (inclusive <c>__Host-</c>) e manda os cabeçalhos que um navegador mandaria.</summary>
internal sealed class Browser(HttpClient client)
{
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    /// <summary>Todos os <c>Set-Cookie</c> já recebidos, crus (para conferir atributos).</summary>
    public List<string> SetCookieHeaders { get; } = [];

    public Task<HttpResponseMessage> GetAsync(string url, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(HttpMethod.Get, url, null, configure);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (Cookies.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        configure?.Invoke(request);

        var response = await client.SendAsync(request).ConfigureAwait(false);
        Remember(response);
        return response;
    }

    /// <summary>Chamada <c>fetch</c> da SPA: mesma origem e, se muda estado, o cabeçalho de CSRF.</summary>
    public Task<HttpResponseMessage> ApiAsync(HttpMethod method, string url, HttpContent? content = null, bool csrf = true, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(method, url, content, request =>
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            if (csrf)
                request.Headers.TryAddWithoutValidation("X-TEC-CSRF", "1");
            configure?.Invoke(request);
        });

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private void Remember(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            return;

        foreach (string header in headers)
        {
            SetCookieHeaders.Add(header);
            string[] parts = header.Split(';', StringSplitOptions.TrimEntries);
            int eq = parts[0].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;

            string name = parts[0][..eq];
            string value = parts[0][(eq + 1)..];
            bool expired = value.Length == 0 || parts.Skip(1).Any(p => IsExpiry(p));
            if (expired)
                Cookies.Remove(name);
            else
                Cookies[name] = value;
        }
    }

    private static bool IsExpiry(string attribute)
    {
        if (attribute.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(attribute[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long age) && age <= 0;
        return attribute.StartsWith("expires=", StringComparison.OrdinalIgnoreCase)
               && DateTimeOffset.TryParse(attribute[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && when <= DateTimeOffset.UtcNow;
    }
}
