using Microsoft.Extensions.Options;

namespace TEC.Bff.Internal;

/// <summary>Confere as opções na subida: configuração insegura ou incoerente impede o host de subir (falha fechada).</summary>
internal sealed class BffOptionsValidator : IValidateOptions<BffOptions>
{
    private static readonly string[] NeverForwarded = ["Cookie", "Authorization", "Proxy-Authorization", "Host", "Set-Cookie"];

    public ValidateOptionsResult Validate(string? name, BffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        ValidateApi(options, failures);

        if (string.IsNullOrWhiteSpace(options.SpaRootPath) || !Directory.Exists(options.SpaRootPath))
            failures.Add("SpaRootPath deve ser uma pasta existente (o build da SPA).");
        else if (!File.Exists(Path.Combine(options.SpaRootPath, "index.html")))
            failures.Add("SpaRootPath deve conter o index.html.");

        if (!IsIdentifier(options.NoncePlaceholder, 8, 64))
            failures.Add("NoncePlaceholder deve ter de 8 a 64 caracteres entre letras, dígitos e '_'.");

        if (options.ImmutablePrefixes is null || options.ImmutablePrefixes.Any(p => p is null || p.Length < 2 || p[0] != '/' || p[^1] != '/' || p.Contains("//", StringComparison.Ordinal)
                                                                                    || p.Contains("..", StringComparison.Ordinal) || HasControlOrSeparator(p, allowSlash: true)))
            failures.Add("ImmutablePrefixes deve conter caminhos que começam e terminam com '/'.");

        if (options.MaxSessions is < 1 or > 1_000_000)
            failures.Add("MaxSessions deve ficar entre 1 e 1.000.000.");
        if (options.SessionLifetime < TimeSpan.FromHours(1) || options.SessionLifetime > TimeSpan.FromHours(24))
            failures.Add("SessionLifetime deve ficar entre 1 e 24 horas.");
        if (options.RefreshSkew < TimeSpan.FromSeconds(5) || options.RefreshSkew > TimeSpan.FromMinutes(5))
            failures.Add("RefreshSkew deve ficar entre 5 segundos e 5 minutos.");
        if (options.RefreshTimeout < TimeSpan.FromSeconds(1) || options.RefreshTimeout > TimeSpan.FromMinutes(1))
            failures.Add("RefreshTimeout deve ficar entre 1 segundo e 1 minuto.");
        if (options.ProxyTimeout < TimeSpan.FromSeconds(1) || options.ProxyTimeout > TimeSpan.FromMinutes(5))
            failures.Add("ProxyTimeout deve ficar entre 1 segundo e 5 minutos.");
        if (options.MaxRequestBodyBytes is < 1 or > 16 * 1024 * 1024)
            failures.Add("MaxRequestBodyBytes deve ficar entre 1 byte e 16 MiB.");
        if (options.HstsMaxAge < TimeSpan.FromDays(1) || options.HstsMaxAge > TimeSpan.FromDays(730))
            failures.Add("HstsMaxAge deve ficar entre 1 dia e 2 anos.");

        if (!IsHeaderToken(options.CsrfHeaderName))
            failures.Add("CsrfHeaderName deve ser um nome de cabeçalho HTTP válido.");

        if (options.ForwardedRequestHeaders is null || options.ForwardedRequestHeaders.Any(h => !IsHeaderToken(h)
                                                                                            || NeverForwarded.Contains(h, StringComparer.OrdinalIgnoreCase)))
            failures.Add("ForwardedRequestHeaders deve conter só nomes de cabeçalho válidos, sem Cookie, Authorization, Proxy-Authorization, Host nem Set-Cookie.");

        if (HasControl(options.ReferrerPolicy) || HasControl(options.PermissionsPolicy) || string.IsNullOrWhiteSpace(options.ReferrerPolicy))
            failures.Add("ReferrerPolicy e PermissionsPolicy não podem ser vazios nem ter caracteres de controle.");

        ValidateCsp(options.Csp, failures);
        ValidateTheme(options.Theme, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateTheme(BffThemeOptions theme, List<string> failures)
    {
        if (theme is null)
        {
            failures.Add("Theme não pode ser nulo.");
            return;
        }

        if (theme.CacheTtl < TimeSpan.FromSeconds(10) || theme.CacheTtl > TimeSpan.FromHours(1))
            failures.Add("Theme.CacheTtl deve ficar entre 10 segundos e 1 hora.");
        if (theme.FetchTimeout < TimeSpan.FromSeconds(1) || theme.FetchTimeout > TimeSpan.FromSeconds(30))
            failures.Add("Theme.FetchTimeout deve ficar entre 1 e 30 segundos.");
        if (theme.StaleMaxAge < theme.CacheTtl || theme.StaleMaxAge > TimeSpan.FromDays(7))
            failures.Add("Theme.StaleMaxAge deve ficar entre Theme.CacheTtl e 7 dias.");
        if (theme.MaxDocumentBytes is < 1024 or > 1024 * 1024)
            failures.Add("Theme.MaxDocumentBytes deve ficar entre 1 KiB e 1 MiB.");
        if (theme.MaxAssetBytes is < 1024 or > 2 * 1024 * 1024)
            failures.Add("Theme.MaxAssetBytes deve ficar entre 1 KiB e 2 MiB.");

        if (theme.Url is not { } url)
            return;
        bool loopback = url.IsAbsoluteUri && url.IsLoopback;
        if (!url.IsAbsoluteUri || !(url.Scheme == Uri.UriSchemeHttps || theme.AllowPrivateNetworks && loopback && url.Scheme == Uri.UriSchemeHttp))
            failures.Add("Theme.Url deve ser uma URL absoluta HTTPS (HTTP só em loopback com Theme.AllowPrivateNetworks).");
        else if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            failures.Add("Theme.Url não pode ter credenciais, consulta nem fragmento.");
        else if (!theme.AllowPrivateNetworks && (loopback || System.Net.IPAddress.TryParse(url.Host, out var ip) && !Theme.ThemeNetwork.IsPublic(ip)))
            failures.Add("Theme.Url não pode apontar para loopback nem rede privada (use Theme.AllowPrivateNetworks só em desenvolvimento).");
    }

    private static void ValidateApi(BffOptions options, List<string> failures)
    {
        var api = options.ApiBaseAddress;
        if (api is null || !api.IsAbsoluteUri)
        {
            failures.Add("ApiBaseAddress é obrigatória e deve ser uma URL absoluta.");
            return;
        }

        if (!string.IsNullOrEmpty(api.UserInfo) || !string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment))
            failures.Add("ApiBaseAddress não pode ter credenciais, query nem fragmento.");
        if (api.Scheme != Uri.UriSchemeHttps && !(api.Scheme == Uri.UriSchemeHttp && api.IsLoopback))
            failures.Add("ApiBaseAddress deve usar HTTPS (HTTP só em loopback).");
        if (api.AbsolutePath != "/")
            failures.Add("ApiBaseAddress deve ser só a origem (sem caminho): o caminho /api/** é mantido.");
    }

    private static void ValidateCsp(BffCspOptions csp, List<string> failures)
    {
        // Os valores entram no cabeçalho da CSP: nada que feche a diretiva ou injete outra
        if (string.IsNullOrWhiteSpace(csp.FrameAncestors) || csp.FrameAncestors.Any(c => c is ';' or ',' or '\r' or '\n' or '"' || char.IsControl(c)))
            failures.Add("Csp.FrameAncestors não pode ser vazio nem conter ';', ',' ou caracteres de controle.");

        foreach (string origin in csp.FormActionOrigins)
        {
            // Entra no cabeçalho da CSP: só origem exata http(s), sem curinga, caminho nem nada que feche a diretiva
            bool valid = Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
                && !origin.Any(c => c is ';' or ',' or '\'' or '"' or '*' or ' ' || char.IsControl(c));
            if (!valid)
                failures.Add("Csp.FormActionOrigins aceita só origens exatas 'http(s)://host[:porta]', sem curinga, caminho, espaços, ';', ',' nem aspas.");
        }

        string endpoint = csp.ReportEndpoint;
        bool relative = endpoint.StartsWith('/') && !endpoint.StartsWith("//", StringComparison.Ordinal);
        bool absolute = endpoint.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(endpoint, UriKind.Absolute, out _);
        if (string.IsNullOrWhiteSpace(endpoint) || !(relative || absolute) || endpoint.Any(c => c is ';' or ',' or '\'' or '"' or ' ' || char.IsControl(c)))
            failures.Add("Csp.ReportEndpoint deve ser um caminho local ('/...') ou uma URL HTTPS absoluta, sem espaços, ';', ',' nem aspas.");
    }

    private static bool IsIdentifier(string? value, int min, int max) =>
        value is not null && value.Length >= min && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsHeaderToken(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool HasControl(string? value) => value is null || value.Any(char.IsControl);

    private static bool HasControlOrSeparator(string value, bool allowSlash) =>
        value.Any(c => char.IsControl(c) || c == '\\' || c == '?' || c == '#' || c == ' ' || (!allowSlash && c == '/'));
}
