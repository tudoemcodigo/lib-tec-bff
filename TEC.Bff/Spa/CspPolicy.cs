namespace TEC.Bff.Spa;

/// <summary>
/// CSP estrita (FRT-SEG-05): <c>script-src</c> só por nonce com <c>strict-dynamic</c>, sem <c>unsafe-inline</c> nem
/// <c>unsafe-eval</c>, <c>object-src 'none'</c>, <c>base-uri 'none'</c>, <c>frame-ancestors</c>, <c>form-action</c> e relatório.
/// </summary>
internal sealed class CspPolicy
{
    public const string NoncePlaceholder = "{nonce}";
    public const string ReportGroup = "bff-csp";

    private readonly string _template;

    public CspPolicy(BffCspOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string style = options.AllowGoogleFonts ? "style-src 'self' https://fonts.googleapis.com" : "style-src 'self'";
        string font = options.AllowGoogleFonts ? "font-src 'self' https://fonts.gstatic.com" : "font-src 'self'";

        _template = string.Join("; ",
            "default-src 'self'",
            $"script-src 'nonce-{NoncePlaceholder}' 'strict-dynamic'",
            style,
            font,
            "img-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'none'",
            "frame-ancestors " + options.FrameAncestors,
            "form-action 'self'" + string.Concat(options.FormActionOrigins.Distinct(StringComparer.Ordinal).Select(o => " " + o)),
            "report-uri " + options.ReportEndpoint,
            "report-to " + ReportGroup);

        HeaderName = options.ReportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy";
        ReportingEndpoints = $"{ReportGroup}=\"{options.ReportEndpoint}\"";
    }

    /// <summary>Nome do cabeçalho (imposto ou só relatório).</summary>
    public string HeaderName { get; }

    /// <summary>Valor de <c>Reporting-Endpoints</c> que dá nome ao destino usado por <c>report-to</c>.</summary>
    public string ReportingEndpoints { get; }

    /// <summary>A política desta resposta, com o nonce novo.</summary>
    public string Build(string nonce) => _template.Replace(NoncePlaceholder, nonce, StringComparison.Ordinal);
}
