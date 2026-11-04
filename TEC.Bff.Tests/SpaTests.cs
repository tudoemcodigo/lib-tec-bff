using System.Net;
using System.Text.RegularExpressions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Hospedagem da SPA: CSP estrita com nonce novo por resposta, cabeçalhos, cache, fallback e arquivos que nunca saem.</summary>
public partial class SpaTests
{
    [GeneratedRegex("'nonce-([A-Za-z0-9_-]+)'")]
    private static partial Regex NonceInCsp();

    private static async Task<(HttpResponseMessage Response, string Body)> GetPageAsync(Browser browser, string path)
    {
        var response = await browser.GetAsync(path, r => r.Headers.TryAddWithoutValidation("Accept", "text/html"));
        return (response, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Index_carries_a_strict_csp_whose_nonce_matches_the_markup()
    {
        await using var app = await BffApp.StartAsync();
        var (response, body) = await GetPageAsync(app.NewBrowser(), "/");

        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        string nonce = NonceInCsp().Match(csp).Groups[1].Value;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/html");
        await Assert.That(nonce.Length).IsGreaterThanOrEqualTo(16);
        await Assert.That(body).Contains($"nonce=\"{nonce}\"");
        await Assert.That(body).DoesNotContain("__CSP_NONCE__");
        await Assert.That(csp).Contains("script-src 'nonce-" + nonce + "' 'strict-dynamic'");
        await Assert.That(csp).Contains("object-src 'none'");
        await Assert.That(csp).Contains("base-uri 'none'");
        await Assert.That(csp).Contains("frame-ancestors 'none'");
        await Assert.That(csp).Contains("form-action 'self'");
        await Assert.That(csp).Contains("report-to bff-csp");
        await Assert.That(csp).Contains("report-uri /bff/csp-report");
        await Assert.That(csp).DoesNotContain("unsafe-inline");
        await Assert.That(csp).DoesNotContain("unsafe-eval");
        await Assert.That(csp).DoesNotContain("google");
        await Assert.That(response.Headers.GetValues("Reporting-Endpoints").Single()).Contains("bff-csp=\"/bff/csp-report\"");
    }

    [Test]
    public async Task Every_response_gets_a_new_unpredictable_nonce()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();
        var nonces = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 200; i++)
        {
            var (response, body) = await GetPageAsync(browser, i % 2 == 0 ? "/" : "/clientes/" + i);
            string nonce = NonceInCsp().Match(response.Headers.GetValues("Content-Security-Policy").Single()).Groups[1].Value;
            await Assert.That(body).Contains(nonce);
            nonces.Add(nonce);
        }

        await Assert.That(nonces.Count).IsEqualTo(200);   // nenhum nonce repetido: reuso anularia a proteção
    }

    [Test]
    public async Task Html_response_carries_the_security_headers()
    {
        await using var app = await BffApp.StartAsync();
        var (response, _) = await GetPageAsync(app.NewBrowser(), "/");

        await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        await Assert.That(response.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("no-referrer");
        await Assert.That(response.Headers.GetValues("Permissions-Policy").Single()).Contains("camera=()");
        await Assert.That(response.Headers.GetValues("Strict-Transport-Security").Single()).IsEqualTo("max-age=31536000; includeSubDomains");
        await Assert.That(response.Headers.CacheControl!.NoCache).IsTrue();
    }

    [Test]
    public async Task Client_routes_fall_back_to_the_index_but_missing_files_do_not()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        foreach (string route in new[] { "/", "/index.html", "/clientes", "/clientes/3f2a/editar", "/pedidos/", "/assets/" })
        {
            var (response, body) = await GetPageAsync(browser, route);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(body).Contains("<div id=\"root\">");
        }

        var (missing, missingBody) = await GetPageAsync(browser, "/assets/nao-existe.js");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(missingBody).DoesNotContain("<div id=\"root\">");
    }

    [Test]
    public async Task Hashed_assets_are_immutable_and_the_rest_revalidates()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var asset = await browser.GetAsync("/assets/app-abc123.js");
        using var css = await browser.GetAsync("/assets/app.css");
        using var icon = await browser.GetAsync("/favicon.ico");
        using var manifest = await browser.GetAsync("/manifest.webmanifest");

        await Assert.That(asset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(asset.Content.Headers.ContentType!.MediaType).IsEqualTo("text/javascript");
        await Assert.That(asset.Headers.CacheControl!.ToString()).IsEqualTo("public, max-age=31536000, immutable");
        await Assert.That(css.Content.Headers.ContentType!.MediaType).IsEqualTo("text/css");
        await Assert.That(icon.Headers.CacheControl!.NoCache).IsTrue();
        await Assert.That(manifest.Content.Headers.ContentType!.MediaType).IsEqualTo("application/manifest+json");
        await Assert.That(asset.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        await Assert.That(await asset.Content.ReadAsStringAsync()).Contains("console.log");
    }

    [Test]
    [Arguments("/assets/app-abc123.js.map")]
    [Arguments("/.env")]
    [Arguments("/assets/.env")]
    [Arguments("/other.html")]
    [Arguments("/assets")]
    public async Task Source_maps_hidden_files_and_other_html_are_never_served(string path)
    {
        await using var app = await BffApp.StartAsync();

        var (status, body, _) = await RawRequest.SendAsync(app, null, "GET", path);

        // "/assets" não tem extensão: é rota do cliente (index); os demais são 404
        if (path == "/assets")
            await Assert.That(status).IsEqualTo(200);
        else
            await Assert.That(status).IsEqualTo(404);
        await Assert.That(body).DoesNotContain("SEGREDO=nao-pode-sair");
        await Assert.That(body).DoesNotContain("\"version\":3");
        await Assert.That(body).DoesNotContain("outra página");
    }

    [Test]
    [Arguments("/../secret.txt", "/../secret.txt")]
    [Arguments("/assets/../../secret.txt", "/assets/../../secret.txt")]
    [Arguments("/../secret.txt", "/%2e%2e/secret.txt")]
    [Arguments("/..\\secret.txt", "/..%5csecret.txt")]
    [Arguments("/assets/..\\..\\secret.txt", "/assets/..\\..\\secret.txt")]
    [Arguments("/./secret.txt", "/./secret.txt")]
    [Arguments("/C:/Windows/win.ini", "/C:/Windows/win.ini")]
    [Arguments("//etc/passwd", "//etc/passwd")]
    public async Task Path_traversal_never_reads_outside_the_build_folder(string path, string raw)
    {
        await using var app = await BffApp.StartAsync();

        var (status, body, _) = await RawRequest.SendAsync(app, null, "GET", path, raw);

        await Assert.That(body).DoesNotContain("FORA-DA-PASTA-DO-BUILD");
        await Assert.That(body).DoesNotContain("[fonts]");
        await Assert.That(body).DoesNotContain("root:");
        await Assert.That(status == 404 || status == 200).IsTrue();   // 200 só se caiu no fallback (a página da SPA), nunca no arquivo
    }

    [Test]
    public async Task Reserved_prefixes_never_fall_back_to_the_spa()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        foreach (string path in new[] { "/bff/desconhecido", "/bff", "/BFF/x", "/api/" })
        {
            var (response, body) = await GetPageAsync(browser, path);
            await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
            await Assert.That(body).DoesNotContain("<div id=\"root\">");
        }

        // "/api" e "/api/**" são do proxy: sem sessão, 401 (nunca a página da SPA)
        var (anonymousApi, apiBody) = await GetPageAsync(browser, "/api");
        await Assert.That(anonymousApi.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(apiBody).DoesNotContain("<div id=\"root\">");
    }

    [Test]
    public async Task Spa_accepts_only_get_and_head()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var post = await browser.SendAsync(HttpMethod.Post, "/clientes", Browser.Json("{}"));
        using var head = await browser.SendAsync(HttpMethod.Head, "/");

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Headers.Contains("Content-Security-Policy")).IsTrue();
        await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Csp_can_start_in_report_only_mode()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Bff = b => b.Csp.ReportOnly = true });
        var (response, _) = await GetPageAsync(app.NewBrowser(), "/");

        await Assert.That(response.Headers.Contains("Content-Security-Policy")).IsFalse();
        await Assert.That(response.Headers.GetValues("Content-Security-Policy-Report-Only").Single()).Contains("script-src 'nonce-");
    }

    [Test]
    public async Task Form_action_is_self_only_by_default_and_gets_the_configured_provider_origins()
    {
        await using (var padrao = await BffApp.StartAsync(new AppSetup()))
        {
            var (resposta, _) = await GetPageAsync(padrao.NewBrowser(), "/");
            string csp = resposta.Headers.GetValues("Content-Security-Policy").Single();
            await Assert.That(DirectiveOf(csp, "form-action")).IsEqualTo("form-action 'self'");
        }

        await using var app = await BffApp.StartAsync(new AppSetup
        {
            Bff = b => b.Csp.FormActionOrigins = ["https://idp.test:8443", "https://idp.test:8443", "http://localhost:8180"],
        });
        var (response, _) = await GetPageAsync(app.NewBrowser(), "/");
        string imposta = response.Headers.GetValues("Content-Security-Policy").Single();

        // Duplicata some; só form-action muda, e script-src continua sem nenhuma origem externa
        await Assert.That(DirectiveOf(imposta, "form-action")).IsEqualTo("form-action 'self' https://idp.test:8443 http://localhost:8180");
        await Assert.That(DirectiveOf(imposta, "script-src")).DoesNotContain("idp.test");
        await Assert.That(DirectiveOf(imposta, "default-src")).IsEqualTo("default-src 'self'");
    }

    private static string DirectiveOf(string csp, string name) =>
        csp.Split(';').Select(d => d.Trim()).First(d => d.StartsWith(name + " ", StringComparison.Ordinal));

    [Test]
    public async Task Google_fonts_are_allowed_only_when_the_option_is_on()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Bff = b => b.Csp.AllowGoogleFonts = true });
        var (response, _) = await GetPageAsync(app.NewBrowser(), "/");

        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        await Assert.That(csp).Contains("style-src 'self' https://fonts.googleapis.com");
        await Assert.That(csp).Contains("font-src 'self' https://fonts.gstatic.com");
        await Assert.That(csp).DoesNotContain("script-src 'self'");
        await Assert.That(csp.Split(';').First(d => d.Trim().StartsWith("script-src", StringComparison.Ordinal))).DoesNotContain("google");
    }

    [Test]
    public async Task Index_without_the_nonce_marker_is_served_with_a_warning()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { IndexWithoutNonce = true });
        var (response, _) = await GetPageAsync(app.NewBrowser(), "/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(app.Logs.Entries.Any(e => e.EventId == 6008)).IsTrue();
    }

    [Test]
    public async Task A_new_build_is_served_without_restarting()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();
        var (_, before) = await GetPageAsync(browser, "/");

        await File.WriteAllTextAsync(Path.Combine(app.SpaDir, "index.html"), "<html><body>NOVO-BUILD <script nonce=\"__CSP_NONCE__\"></script></body></html>");
        File.SetLastWriteTimeUtc(Path.Combine(app.SpaDir, "index.html"), DateTime.UtcNow.AddMinutes(1));
        var (_, after) = await GetPageAsync(browser, "/");

        await Assert.That(before).DoesNotContain("NOVO-BUILD");
        await Assert.That(after).Contains("NOVO-BUILD");
    }

    [Test]
    public async Task Hostile_responses_never_reflect_the_request()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        var (response, body) = await GetPageAsync(browser, "/clientes?q=%3Cscript%3Ealert(1)%3C/script%3E");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).DoesNotContain("alert(1)");
    }
}
