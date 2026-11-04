using Microsoft.AspNetCore.Http;

namespace TEC.Bff.Internal;

/// <summary>
/// Defesa contra CSRF para uma sessão em cookie: <c>SameSite=Lax</c> (do TEC.Security) mais (a) cabeçalho customizado, que um site
/// de outra origem não consegue enviar sem CORS (o BFF nunca o libera), e (b) prova de mesma origem por <c>Sec-Fetch-Site</c> ou <c>Origin</c>.
/// </summary>
internal static class CsrfGuard
{
    public static bool ChangesState(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    /// <summary>
    /// <c>false</c> se o navegador diz que a requisição veio de outro site. Sem <c>Sec-Fetch-Site</c> nem <c>Origin</c> (cliente que
    /// não é navegador) não há o que contestar aqui: o cabeçalho customizado é quem decide.
    /// </summary>
    public static bool NotCrossSite(HttpRequest request)
    {
        string? fetchSite = request.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(fetchSite))
            return string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);

        string? origin = request.Headers.Origin;
        return string.IsNullOrEmpty(origin) || IsOwnOrigin(request, origin);
    }

    /// <summary>Prova positiva de mesma origem (logout por formulário não consegue enviar o cabeçalho customizado).</summary>
    public static bool ProvenSameOrigin(HttpRequest request)
    {
        string? fetchSite = request.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(fetchSite))
            return string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);

        string? origin = request.Headers.Origin;
        return !string.IsNullOrEmpty(origin) && IsOwnOrigin(request, origin);
    }

    public static bool HasHeader(HttpRequest request, string headerName) =>
        request.Headers.TryGetValue(headerName, out var values) && values.Count == 1 && string.Equals(values[0], "1", StringComparison.Ordinal);

    private static bool IsOwnOrigin(HttpRequest request, string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Caminho local seguro para voltar depois do login, ou <c>/</c> (evita open redirect).</summary>
    public static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 2048 || returnUrl[0] != '/')
            return "/";

        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
            return "/";

        return returnUrl.Any(c => char.IsControl(c) || c == '\\') ? "/" : returnUrl;
    }
}
