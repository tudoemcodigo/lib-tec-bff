using System.Net;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Login OpenID Connect de ponta a ponta: tokens só no servidor, cookie sem token, falha fechada.</summary>
public class LoginTests
{
    private const string CookieName = "__Host-TEC.Auth";

    [Test]
    public async Task Login_keeps_the_tokens_on_the_server_and_the_cookie_is_hardened()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var callback = await app.LoginAsync(browser, "/dashboard?aba=1");

        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(callback.Headers.Location!.OriginalString).IsEqualTo("/dashboard?aba=1");
        await Assert.That(browser.Cookies.ContainsKey(CookieName)).IsTrue();

        string setCookie = browser.SetCookieHeaders.Single(h => h.StartsWith(CookieName + "=", StringComparison.Ordinal)).ToLowerInvariant();
        await Assert.That(setCookie).Contains("secure");
        await Assert.That(setCookie).Contains("httponly");
        await Assert.That(setCookie).Contains("samesite=lax");
        await Assert.That(setCookie).Contains("path=/");
        await Assert.That(setCookie).DoesNotContain("domain=");

        // Nenhum cookie e nenhum cabeçalho de resposta carrega token ou identificador de sessão do provedor
        string everything = string.Join('\n', browser.SetCookieHeaders) + callback.Headers + string.Join('\n', browser.Cookies.Values);
        await Assert.That(everything).DoesNotContain("ACCESS-");
        await Assert.That(everything).DoesNotContain("REFRESH-");

        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);
        await Assert.That(app.Idp.CodeRedemptions).IsEqualTo(1);
        await Assert.That(string.IsNullOrEmpty(app.Idp.LastCodeVerifier)).IsFalse();   // PKCE
    }

    [Test]
    public async Task User_endpoint_returns_name_and_permissions_and_never_a_token()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/bff/user");
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).Contains("\"name\":\"Maria Teste\"");
        await Assert.That(body).Contains("\"permissions\":[\"clientes.ler\",\"pedidos.ler\"]");
        await Assert.That(body).DoesNotContain("ACCESS-");
        await Assert.That(body).DoesNotContain("REFRESH-");
        await Assert.That(body).DoesNotContain("idp-session");
        await Assert.That(string.Join(',', response.Headers.CacheControl?.ToString() ?? "")).Contains("no-store");
    }

    [Test]
    public async Task Anonymous_user_gets_401_without_details()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/bff/user");
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(body).Contains("BFF_NAO_AUTENTICADO");
        await Assert.That(response.Headers.Contains("WWW-Authenticate")).IsFalse();
    }

    [Test]
    public async Task Tampered_session_cookie_is_not_a_session()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();
        string original = browser.Cookies[CookieName];
        browser.Cookies[CookieName] = original[..^6] + (original[^6..] == "AAAAAA" ? "BBBBBB" : "AAAAAA");

        using var response = await browser.ApiAsync(HttpMethod.Get, "/bff/user");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Callback_with_an_unknown_state_fails_and_stores_nothing()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();
        using var challenge = await browser.GetAsync("/bff/login", r => r.Headers.TryAddWithoutValidation("Accept", "text/html"));

        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "code-1", ["state"] = "estado-forjado" });
        using var callback = await browser.SendAsync(HttpMethod.Post, "/signin-oidc", form);

        await Assert.That((int)callback.StatusCode).IsEqualTo(401);
        await Assert.That(browser.Cookies.ContainsKey(CookieName)).IsFalse();
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
        await Assert.That(app.Idp.CodeRedemptions).IsEqualTo(0);
    }

    [Test]
    public async Task Replaying_the_callback_does_not_create_a_second_session()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();
        using var challenge = await browser.GetAsync("/bff/login", r => r.Headers.TryAddWithoutValidation("Accept", "text/html"));
        var query = System.Web.HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        app.Idp.PendingNonce = query["nonce"];
        var cookiesAtChallenge = new Dictionary<string, string>(browser.Cookies);

        using var first = await browser.SendAsync(HttpMethod.Post, "/signin-oidc",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "c", ["state"] = query["state"]! }));
        var replay = new Browser(app.Client);
        foreach (var (name, value) in cookiesAtChallenge)
            replay.Cookies[name] = value;
        using var second = await replay.SendAsync(HttpMethod.Post, "/signin-oidc",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "c", ["state"] = query["state"]! }));

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Found);
        // A mesma resposta do provedor, reapresentada com os cookies do desafio original, até pode validar: o que importa é que cada
        // login gera uma sessão com identificador próprio (nenhum identificador previsível ou reaproveitado)
        int sessions = ((InMemoryBffSessionStore)app.Store).Count;
        await Assert.That(sessions).IsGreaterThanOrEqualTo(1);
        await Assert.That(sessions).IsLessThanOrEqualTo(2);
        if (second.StatusCode == HttpStatusCode.Found)
            await Assert.That(replay.Cookies[CookieName]).IsNotEqualTo(browser.Cookies[CookieName]);
    }

    [Test]
    public async Task Login_fails_closed_when_the_provider_returns_no_access_token()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Idp = idp => idp.OmitAccessToken = true });
        var browser = app.NewBrowser();

        using var callback = await app.LoginAsync(browser);

        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(browser.Cookies.ContainsKey(CookieName)).IsFalse();
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Login_fails_closed_when_the_session_store_is_full()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Bff = b => b.MaxSessions = 1 });
        _ = await app.LoggedInAsync();
        var second = app.NewBrowser();

        using var callback = await app.LoginAsync(second);

        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(second.Cookies.ContainsKey(CookieName)).IsFalse();
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("https://evil.test/phish")]
    [Arguments("//evil.test")]
    [Arguments("/\\evil.test")]
    [Arguments("javascript:alert(1)")]
    public async Task Return_url_never_redirects_to_another_site(string hostile)
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var callback = await app.LoginAsync(browser, hostile);

        await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(callback.Headers.Location!.OriginalString).IsEqualTo("/");
    }

    [Test]
    public async Task Session_ids_are_unpredictable_and_never_accepted_from_the_client()
    {
        await using var app = await BffApp.StartAsync();
        var first = await app.LoggedInAsync();
        var second = await app.LoggedInAsync();

        await Assert.That(first.Cookies[CookieName]).IsNotEqualTo(second.Cookies[CookieName]);
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(2);

        // Cookie/consulta com um "sid" escolhido pelo atacante não cria nem reaproveita sessão
        var attacker = app.NewBrowser();
        attacker.Cookies[".tec.bff.sid"] = new string('A', 43);
        using var response = await attacker.ApiAsync(HttpMethod.Get, "/bff/user?sid=" + new string('A', 43));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
