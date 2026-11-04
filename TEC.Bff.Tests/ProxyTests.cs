using System.Net;
using System.Text;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Proxy <c>/api/**</c>: bearer da sessão, nada do navegador atravessa, CSRF, limites e caminhos hostis.</summary>
public class ProxyTests
{
    [Test]
    public async Task Proxy_attaches_the_session_bearer_and_passes_the_response_through()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes?pagina=2&tamanho=10");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var call = app.Api.Calls.Single();
        await Assert.That(call.PathAndQuery).IsEqualTo("/api/v1/clientes?pagina=2&tamanho=10");
        await Assert.That(call.Header("Authorization")).IsEqualTo("Bearer ACCESS-1");
        await Assert.That(response.Headers.GetValues("ETag").Single()).IsEqualTo("\"v1\"");
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("\"ok\":true");
    }

    [Test]
    public async Task Proxy_forwards_only_allowed_headers_and_never_a_browser_credential()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Put, "/api/v1/clientes/1", Browser.Json("{\"nome\":\"x\"}"), configure: r =>
        {
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer ATACANTE");
            r.Headers.TryAddWithoutValidation("X-Api-Key", "chave-do-atacante");
            r.Headers.TryAddWithoutValidation("X-Forwarded-For", "6.6.6.6");
            r.Headers.TryAddWithoutValidation("Forwarded", "for=6.6.6.6");
            r.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
            r.Headers.TryAddWithoutValidation("Idempotency-Key", "0f8f1b02-4a41-4a4e-a7f4-1f2f3a4b5c6d");
            r.Headers.TryAddWithoutValidation("X-Correlation-ID", "corr-1");
            r.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR");
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var call = app.Api.Calls.Single();
        await Assert.That(call.Header("Authorization")).IsEqualTo("Bearer ACCESS-1");
        await Assert.That(call.Header("Cookie")).IsNull();
        await Assert.That(call.Header("X-Api-Key")).IsNull();
        await Assert.That(call.Header("X-Forwarded-For")).IsNull();
        await Assert.That(call.Header("Forwarded")).IsNull();
        await Assert.That(call.Header("X-TEC-CSRF")).IsNull();
        await Assert.That(call.Header("If-Match")).IsEqualTo("\"v1\"");
        await Assert.That(call.Header("Idempotency-Key")).IsEqualTo("0f8f1b02-4a41-4a4e-a7f4-1f2f3a4b5c6d");
        await Assert.That(call.Header("X-Correlation-ID")).IsEqualTo("corr-1");
        await Assert.That(call.Header("Accept-Language")).IsEqualTo("pt-BR");
        await Assert.That(Encoding.UTF8.GetString(call.Body)).IsEqualTo("{\"nome\":\"x\"}");
    }

    [Test]
    public async Task Proxy_drops_cookies_and_server_banner_set_by_the_api()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");

        await Assert.That(response.Headers.Contains("Set-Cookie")).IsFalse();
        await Assert.That(response.Headers.Contains("Server")).IsFalse();
        await Assert.That(browser.Cookies.ContainsKey("evil")).IsFalse();
    }

    [Test]
    public async Task Proxy_without_a_session_is_401_and_never_reaches_the_api()
    {
        await using var app = await BffApp.StartAsync();
        var anonymous = app.NewBrowser();

        using var response = await anonymous.ApiAsync(HttpMethod.Get, "/api/v1/clientes");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task State_changing_calls_without_the_csrf_header_are_refused()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var response = await browser.ApiAsync(method, "/api/v1/clientes/1", Browser.Json("{}"), csrf: false);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("BFF_CSRF");
        }

        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("0")]
    [Arguments("true")]
    [Arguments("")]
    [Arguments("1, 1")]
    public async Task Csrf_header_must_be_exactly_one(string value)
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", Browser.Json("{}"), csrf: false,
            configure: r => r.Headers.TryAddWithoutValidation("X-TEC-CSRF", value));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cross_site_requests_are_refused_even_with_the_csrf_header()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var crossSite = await browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", Browser.Json("{}"),
            configure: r =>
            {
                r.Headers.Remove("Sec-Fetch-Site");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            });
        using var evilOrigin = await browser.SendAsync(HttpMethod.Post, "/api/v1/clientes", Browser.Json("{}"), r =>
        {
            r.Headers.TryAddWithoutValidation("X-TEC-CSRF", "1");
            r.Headers.TryAddWithoutValidation("Origin", "https://evil.test");
        });

        await Assert.That(crossSite.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(evilOrigin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Safe_methods_do_not_need_the_csrf_header()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes", csrf: false);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Body_larger_than_the_limit_is_413_by_content_length_and_by_chunked_stream()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Bff = b => b.MaxRequestBodyBytes = 1024 });
        var browser = await app.LoggedInAsync();

        using var declared = await browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", new ByteArrayContent(new byte[2048]));
        using var chunked = await browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", new UnknownLengthContent(new byte[4096]));
        using var withinLimit = await browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", new ByteArrayContent(new byte[512]));

        await Assert.That(declared.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(chunked.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(withinLimit.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("/api/../bff/user", "/api/../bff/user")]
    [Arguments("/api/../admin", "/api/../admin")]
    [Arguments("/api/./clientes", "/api/./clientes")]
    [Arguments("/api/../secret", "/api/%2e%2e/secret")]
    [Arguments("/api/../x", "/api/..%2fx")]
    [Arguments("/api/a/b", "/api/a%5cb")]
    [Arguments("/api/a\\b", "/api/a\\b")]
    [Arguments("/api/a\0b", "/api/a%00b")]
    public async Task Paths_that_escape_the_api_prefix_are_never_forwarded(string path, string raw)
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        var (status, _, _) = await RawRequest.SendAsync(app, browser, "GET", path, raw);

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Methods_outside_the_allowed_list_are_not_proxied()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        var (trace, _, _) = await RawRequest.SendAsync(app, browser, "TRACE", "/api/v1/clientes");

        await Assert.That(trace).IsEqualTo(405);
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Upstream_failure_is_502_without_internal_details()
    {
        await using var app = await BffApp.StartAsync();
        app.Api.Respond = (_, _) => throw new HttpRequestException("conexao recusada em 10.0.0.7:5432 segredo-interno-xyz");
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That((int)response.StatusCode).IsGreaterThanOrEqualTo(500);
        await Assert.That(body).Contains("BFF_ERRO_UPSTREAM");
        await Assert.That(body).DoesNotContain("10.0.0.7");
        await Assert.That(body).DoesNotContain("segredo-interno");
        // O YARP registra a exceção de rede do .NET (host e porta); o que nunca pode ir ao log é credencial
        await Assert.That(app.Logs.All).DoesNotContain("ACCESS-");
        await Assert.That(app.Logs.All).DoesNotContain("REFRESH-");
    }

    [Test]
    public async Task Slow_upstream_hits_the_proxy_timeout()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Bff = b => b.ProxyTimeout = TimeSpan.FromSeconds(1) });
        app.Api.Respond = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var browser = await app.LoggedInAsync();

        var started = DateTime.UtcNow;
        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");

        await Assert.That((int)response.StatusCode).IsGreaterThanOrEqualTo(500);
        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Api_status_codes_are_not_rewritten()
    {
        await using var app = await BffApp.StartAsync();
        app.Api.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"success\":false}") });
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Put, "/api/v1/clientes/1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Parallel_calls_from_many_users_never_mix_tokens()
    {
        await using var app = await BffApp.StartAsync();
        var browsers = new List<Browser>();
        for (int i = 0; i < 12; i++)
            browsers.Add(await app.LoggedInAsync());

        var results = await Task.WhenAll(browsers.Select(async b =>
        {
            using var r = await b.ApiAsync(HttpMethod.Get, "/api/v1/clientes");
            return r.StatusCode;
        }));

        await Assert.That(results.All(s => s == HttpStatusCode.OK)).IsTrue();
        var tokens = app.Api.Calls.Select(c => c.Header("Authorization")).ToList();
        await Assert.That(tokens.Distinct().Count()).IsEqualTo(12);   // um bearer por sessão
    }

    /// <summary>Corpo sem <c>Content-Length</c> (vai em <c>chunked</c>), que o pré-teste de tamanho não enxerga.</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data, 0, data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
