using System.Net;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Renovação do access token: single-flight, rotação, recusa, provedor fora ou lento, segredo do cofre.</summary>
public class RefreshTests
{
    // O access token dura 5 min e o BFF renova 30 s antes de vencer: a 4m40s faltam 20 s
    private static readonly TimeSpan NearExpiry = TimeSpan.FromSeconds(280);
    private static readonly TimeSpan PastExpiry = TimeSpan.FromSeconds(310);

    private static async Task<HttpStatusCode> CallAsync(Browser browser)
    {
        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");
        return response.StatusCode;
    }

    [Test]
    public async Task Token_is_renewed_before_it_expires_and_the_rotated_refresh_token_is_used_next()
    {
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();
        await CallAsync(browser);
        await Assert.That(app.Api.Calls[0].Header("Authorization")).IsEqualTo("Bearer ACCESS-1");
        await Assert.That(app.Idp.RefreshCalls).IsEqualTo(0);

        app.Clock.Advance(NearExpiry);
        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(app.Api.Calls[1].Header("Authorization")).IsEqualTo("Bearer ACCESS-2");

        app.Clock.Advance(NearExpiry);
        await CallAsync(browser);

        await Assert.That(app.Idp.RefreshCalls).IsEqualTo(2);
        await Assert.That(app.Idp.ReceivedRefreshTokens[0]).IsEqualTo("REFRESH-1");
        await Assert.That(app.Idp.ReceivedRefreshTokens[1]).IsEqualTo("REFRESH-2");   // rotação respeitada
        await Assert.That(app.Api.Calls[2].Header("Authorization")).IsEqualTo("Bearer ACCESS-3");
    }

    [Test]
    public async Task Concurrent_calls_share_a_single_refresh()
    {
        await using var app = await BffApp.StartAsync();
        app.Idp.RefreshBehavior = async (_, ct) =>
        {
            await Task.Delay(300, ct);
            return FakeIdp.Json(app.Idp.TokenBody(includeIdToken: false));
        };
        var browser = await app.LoggedInAsync();
        app.Clock.Advance(NearExpiry);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => CallAsync(browser)));

        await Assert.That(statuses.All(s => s == HttpStatusCode.OK)).IsTrue();
        await Assert.That(app.Idp.RefreshCalls).IsEqualTo(1);   // um refresh token rotativo não sobrevive a dois usos
        await Assert.That(app.Api.Calls.Select(c => c.Header("Authorization")).Distinct().Single()).IsEqualTo("Bearer ACCESS-2");
    }

    [Test]
    public async Task Refresh_refused_by_the_provider_ends_the_session()
    {
        await using var app = await BffApp.StartAsync();
        app.Idp.RefreshBehavior = (_, _) => Task.FromResult(FakeIdp.Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest));
        var browser = await app.LoggedInAsync();
        app.Clock.Advance(NearExpiry);

        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Provider_down_keeps_a_still_valid_token_and_recovers_by_itself()
    {
        await using var app = await BffApp.StartAsync();
        bool down = true;
        app.Idp.RefreshBehavior = (_, _) => down
            ? throw new HttpRequestException("idp fora do ar")
            : Task.FromResult(FakeIdp.Json(app.Idp.TokenBody(includeIdToken: false)));
        var browser = await app.LoggedInAsync();

        app.Clock.Advance(NearExpiry);
        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.OK);   // ainda vale por 20 s
        await Assert.That(app.Api.Calls[0].Header("Authorization")).IsEqualTo("Bearer ACCESS-1");

        app.Clock.Advance(TimeSpan.FromSeconds(30));   // venceu
        using var unavailable = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(unavailable.Headers.RetryAfter).IsNotNull();
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);   // a sessão não é derrubada pela falha do provedor

        down = false;   // o provedor volta
        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(app.Api.Calls[^1].Header("Authorization")).StartsWith("Bearer ACCESS-");
        await Assert.That(app.Api.Calls[^1].Header("Authorization")).IsNotEqualTo("Bearer ACCESS-1");
    }

    [Test]
    public async Task Slow_provider_is_cut_by_the_refresh_timeout()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Bff = b => b.RefreshTimeout = TimeSpan.FromSeconds(1) });
        app.Idp.RefreshBehavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            return FakeIdp.Json("{}");
        };
        var browser = await app.LoggedInAsync();
        app.Clock.Advance(PastExpiry);

        var started = DateTime.UtcNow;
        var status = await CallAsync(browser);

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task Hostile_provider_responses_are_unavailable_never_a_crash_or_a_bad_token()
    {
        string huge = new('x', 70 * 1024);
        string[] bodies =
        [
            "not json at all",
            "{}",
            "{\"access_token\":\"A\",\"token_type\":\"mac\",\"expires_in\":300}",
            "{\"access_token\":\"\",\"token_type\":\"Bearer\",\"expires_in\":300}",
            "{\"access_token\":\"" + new string('a', 17 * 1024) + "\",\"token_type\":\"Bearer\",\"expires_in\":300}",
            "{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"padding\":\"" + huge + "\"}",
            "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[["
        ];

        foreach (string body in bodies)
        {
            await using var app = await BffApp.StartAsync();
            app.Idp.RefreshBehavior = (_, _) => Task.FromResult(FakeIdp.Json(body));
            var browser = await app.LoggedInAsync();
            app.Clock.Advance(PastExpiry);

            await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(app.Api.Calls.Count).IsEqualTo(0);
            await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Provider_5xx_and_429_keep_the_session()
    {
        foreach (var status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.TooManyRequests })
        {
            await using var app = await BffApp.StartAsync();
            app.Idp.RefreshBehavior = (_, _) => Task.FromResult(new HttpResponseMessage(status));
            var browser = await app.LoggedInAsync();
            app.Clock.Advance(PastExpiry);

            await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Session_without_a_refresh_token_ends_when_the_access_token_expires()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Idp = idp => idp.OmitRefreshToken = true });
        var browser = await app.LoggedInAsync();

        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.OK);
        app.Clock.Advance(NearExpiry);
        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.OK);   // ainda não venceu
        app.Clock.Advance(TimeSpan.FromSeconds(40));

        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
        await Assert.That(app.Idp.RefreshCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Session_ends_at_the_absolute_lifetime_even_with_valid_tokens()
    {
        await using var app = await BffApp.StartAsync(new AppSetup { Idp = idp => idp.AccessLifetime = TimeSpan.FromHours(48), Bff = b => b.SessionLifetime = TimeSpan.FromHours(1) });
        var browser = await app.LoggedInAsync();
        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.OK);

        app.Clock.Advance(TimeSpan.FromMinutes(61));

        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Client_secret_is_read_from_the_vault_on_every_refresh_and_never_leaks()
    {
        var vault = new FakeSecretReader("segredo-v1-cofre");
        await using var app = await BffApp.StartAsync(new AppSetup { Vault = vault });
        var browser = await app.LoggedInAsync();

        vault.Value = "segredo-v2-rotacionado";   // rotação no cofre, sem reiniciar
        app.Clock.Advance(NearExpiry);
        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(app.Idp.ReceivedClientSecrets[0]).IsEqualTo("segredo-v1-cofre");     // troca do código
        await Assert.That(app.Idp.ReceivedClientSecrets[^1]).IsEqualTo("segredo-v2-rotacionado");   // renovação
        string everything = body + app.Logs.All + string.Join('\n', browser.SetCookieHeaders);
        await Assert.That(everything).DoesNotContain("segredo-v1-cofre");
        await Assert.That(everything).DoesNotContain("segredo-v2-rotacionado");
    }

    [Test]
    public async Task Vault_failure_during_refresh_is_unavailable_without_leaking_or_crashing()
    {
        var vault = new FakeSecretReader("segredo-v1-cofre");
        await using var app = await BffApp.StartAsync(new AppSetup { Vault = vault });
        var browser = await app.LoggedInAsync();

        vault.Value = null;   // cofre sem o segredo
        app.Clock.Advance(PastExpiry);

        await Assert.That(await CallAsync(browser)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(app.Idp.RefreshCalls).IsEqualTo(0);   // sem segredo não há pedido ao provedor
        await Assert.That(app.Logs.All).DoesNotContain("segredo-v1-cofre");
    }

    [Test]
    public async Task Refresh_client_does_not_follow_redirects_or_keep_cookies()
    {
        using var refresh = BffExtensions.CreateHandler();

        await Assert.That(refresh.AllowAutoRedirect).IsFalse();   // um 307 reenviaria o client secret a outro destino
        await Assert.That(refresh.UseCookies).IsFalse();
        await Assert.That(refresh.UseProxy).IsFalse();
    }
}
