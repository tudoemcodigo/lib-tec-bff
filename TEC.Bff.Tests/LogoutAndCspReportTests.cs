using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Logout (CSRF de logout, sessão apagada no servidor) e relatórios de violação da CSP.</summary>
public class LogoutAndCspReportTests
{
    [Test]
    public async Task Logout_removes_the_server_session_clears_the_cookie_and_goes_to_the_provider()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var logout = await browser.SendAsync(HttpMethod.Post, "/bff/logout", null, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin"));

        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(logout.Headers.Location!.OriginalString).StartsWith(FakeIdp.Authority + "/protocol/openid-connect/logout");
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
        await Assert.That(browser.Cookies.ContainsKey("__Host-TEC.Auth")).IsFalse();
        using var afterwards = await browser.ApiAsync(HttpMethod.Get, "/bff/user");
        await Assert.That(afterwards.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_stolen_cookie_is_useless_after_logout()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();
        var thief = app.NewBrowser();
        thief.Cookies["__Host-TEC.Auth"] = browser.Cookies["__Host-TEC.Auth"];
        using var before = await thief.ApiAsync(HttpMethod.Get, "/bff/user");

        using var logout = await browser.SendAsync(HttpMethod.Post, "/bff/logout", null, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin"));
        // /bff/user primeiro: a chamada de proxy sem sessão também limpa o cookie do navegador
        using var afterUser = await thief.ApiAsync(HttpMethod.Get, "/bff/user");
        thief.Cookies["__Host-TEC.Auth"] = browser.SetCookieHeaders.First(h => h.StartsWith("__Host-TEC.Auth=", StringComparison.Ordinal)).Split(';')[0]["__Host-TEC.Auth=".Length..];
        using var after = await thief.ApiAsync(HttpMethod.Get, "/api/v1/clientes");

        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(afterUser.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);   // o cookie ainda é válido, mas a sessão no servidor acabou
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cross_site_or_unproven_logout_is_refused()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var crossSite = await browser.SendAsync(HttpMethod.Post, "/bff/logout", null, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"));
        using var evilOrigin = await browser.SendAsync(HttpMethod.Post, "/bff/logout", null, r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.test"));
        using var noProof = await browser.SendAsync(HttpMethod.Post, "/bff/logout");
        using var viaGet = await browser.SendAsync(HttpMethod.Get, "/bff/logout");

        await Assert.That(crossSite.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(evilOrigin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(noProof.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(viaGet.StatusCode).IsNotEqualTo(HttpStatusCode.Found);   // GET não desloga (<img src="/bff/logout">)
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);
    }

    private static StringContent Report(string json, string type = "application/csp-report") => new(json, Encoding.UTF8, type);

    private static MeterListener Listen(List<string> directives)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BffDiagnostics.MeterName && instrument.Name == "tec.bff.csp.violations")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                lock (directives)
                    directives.Add(tag.Value?.ToString() ?? "");
            }
        });
        listener.Start();
        return listener;
    }

    [Test]
    public async Task Csp_reports_are_counted_by_directive_and_their_content_is_never_logged()
    {
        await using var app = await BffApp.StartAsync();
        var seen = new List<string>();
        using var listener = Listen(seen);
        var browser = app.NewBrowser();

        using var legacy = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report(
            "{\"csp-report\":{\"effective-directive\":\"script-src-elem\",\"blocked-uri\":\"https://x.test/?email=maria@exemplo.com\"}}"));
        using var modern = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report(
            "[{\"type\":\"csp-violation\",\"body\":{\"effectiveDirective\":\"img-src\",\"blockedURL\":\"https://y.test/cpf=123.456.789-09\"}}]", "application/reports+json"));
        using var unknown = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report("{\"csp-report\":{\"violated-directive\":\"invento-src\"}}"));
        listener.Dispose();

        await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(modern.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(seen.Contains("script-src-elem")).IsTrue();
        await Assert.That(seen.Contains("img-src")).IsTrue();
        await Assert.That(seen.Contains("outra")).IsTrue();   // rótulo de lista fechada: sem cardinalidade ilimitada
        await Assert.That(seen.Contains("invento-src")).IsFalse();
        await Assert.That(app.Logs.All).DoesNotContain("maria@exemplo.com");
        await Assert.That(app.Logs.All).DoesNotContain("123.456.789-09");
    }

    [Test]
    public async Task Csp_report_endpoint_rejects_oversize_wrong_type_and_garbage_without_crashing()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var huge = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report("{\"csp-report\":{\"x\":\"" + new string('a', 20 * 1024) + "\"}}"));
        using var chunkedHuge = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", new StreamContent(new MemoryStream(new byte[40 * 1024]))
        {
            Headers = { ContentType = new("application/csp-report") }
        });
        using var wrongType = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", new StringContent("<x/>", Encoding.UTF8, "text/xml"));
        using var garbage = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report("não é json"));
        using var deep = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report(new string('[', 5000) + new string(']', 5000)));
        using var manyReports = await browser.SendAsync(HttpMethod.Post, "/bff/csp-report", Report(
            "[" + string.Join(',', Enumerable.Repeat("{\"body\":{\"effectiveDirective\":\"img-src\"}}", 200)) + "]", "application/reports+json"));

        await Assert.That(huge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(chunkedHuge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(wrongType.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        await Assert.That(garbage.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(deep.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(manyReports.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }
}
