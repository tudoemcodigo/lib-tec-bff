using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Token, segredo e identificador de sessão nunca em resposta, cookie, log, métrica ou rótulo; telemetria dos desfechos.</summary>
public class LeakageAndTelemetryTests
{
    private static MeterListener ListenAll(ConcurrentQueue<(string Instrument, string Tags)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BffDiagnostics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, string.Join(',', tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.Start();
        return listener;
    }

    [Test]
    public async Task No_flow_leaks_tokens_secrets_or_the_session_id()
    {
        var vault = new FakeSecretReader("segredo-do-cofre-xyz");
        await using var app = await BffApp.StartAsync(new AppSetup { Vault = vault });
        var collected = new List<string>();
        var browser = app.NewBrowser();

        // Fluxo completo: login, usuário, proxy, renovação, renovação recusada, 401, CSRF, corpo grande, logout
        using (var callback = await app.LoginAsync(browser))
            collected.Add(callback.Headers.ToString());
        foreach (var call in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => browser.ApiAsync(HttpMethod.Get, "/bff/user"),
                     () => browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes"),
                     () => browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", Browser.Json("{}"), csrf: false),
                     () => browser.ApiAsync(HttpMethod.Post, "/api/v1/clientes", new ByteArrayContent(new byte[2 * 1024 * 1024]))
                 })
        {
            using var response = await call();
            collected.Add(response.Headers + await response.Content.ReadAsStringAsync());
        }

        app.Clock.Advance(TimeSpan.FromSeconds(280));
        using (var refreshed = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes"))
            collected.Add(refreshed.Headers + await refreshed.Content.ReadAsStringAsync());

        app.Idp.RefreshBehavior = (_, _) => Task.FromResult(FakeIdp.Json("{\"error\":\"invalid_grant\",\"error_description\":\"Token is not active\"}", HttpStatusCode.BadRequest));
        app.Clock.Advance(TimeSpan.FromSeconds(280));
        using (var refused = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes"))
            collected.Add(refused.Headers + await refused.Content.ReadAsStringAsync());

        var second = await app.LoggedInAsync();
        using (var logout = await second.SendAsync(HttpMethod.Post, "/bff/logout", null, r => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin")))
            collected.Add(logout.Headers.ToString());

        string everything = string.Join('\n', collected) + "\n" + app.Logs.All + "\n" + string.Join('\n', browser.SetCookieHeaders) + string.Join('\n', second.SetCookieHeaders);
        await Assert.That(app.Logs.Entries.Count).IsGreaterThan(10);   // o log existiu e foi inspecionado
        await Assert.That(everything).DoesNotContain("ACCESS-");
        await Assert.That(everything).DoesNotContain("REFRESH-");
        await Assert.That(everything).DoesNotContain("segredo-do-cofre-xyz");
        await Assert.That(everything).DoesNotContain("Token is not active");   // detalhe do provedor não chega ao cliente
        await Assert.That(everything).DoesNotContain(FakeIdp.ClientSecret);
        await Assert.That(everything).DoesNotContain("idp-session-1");
    }

    [Test]
    public async Task Outcomes_are_counted_with_low_cardinality_labels()
    {
        var measurements = new ConcurrentQueue<(string Instrument, string Tags)>();
        using var listener = ListenAll(measurements);
        await using var app = await BffApp.StartAsync();
        var anonymous = app.NewBrowser();
        var browser = await app.LoggedInAsync();

        using (await anonymous.ApiAsync(HttpMethod.Get, "/api/v1/x")) { }
        using (await browser.ApiAsync(HttpMethod.Get, "/api/v1/x")) { }
        using (await browser.ApiAsync(HttpMethod.Post, "/api/v1/x", Browser.Json("{}"), csrf: false)) { }
        app.Clock.Advance(TimeSpan.FromSeconds(280));
        using (await browser.ApiAsync(HttpMethod.Get, "/api/v1/x")) { }
        listener.Dispose();

        string all = string.Join('\n', measurements.Select(m => m.Instrument + ":" + m.Tags));
        await Assert.That(all).Contains("tec.bff.login.completed:outcome=ok");
        await Assert.That(all).Contains("tec.bff.proxy.requests:outcome=nao_autenticado");
        await Assert.That(all).Contains("tec.bff.proxy.requests:outcome=ok");
        await Assert.That(all).Contains("tec.bff.proxy.requests:outcome=csrf");
        await Assert.That(all).Contains("tec.bff.token.refresh:outcome=renovado");
        await Assert.That(all).DoesNotContain("ACCESS-");
        await Assert.That(all).DoesNotContain("user-1");   // nenhum rótulo identifica o usuário
    }

    [Test]
    public async Task Activities_are_published_without_tokens_in_tags()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BffDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await BffApp.StartAsync();
        var browser = await app.LoggedInAsync();
        app.Clock.Advance(TimeSpan.FromSeconds(280));

        using (await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes")) { }

        var names = activities.Select(a => a.OperationName).ToList();
        await Assert.That(names).Contains("TEC.Bff.Proxy");
        await Assert.That(names).Contains("TEC.Bff.RefreshToken");
        string tags = string.Join(',', activities.SelectMany(a => a.Tags).Select(t => t.Key + "=" + t.Value));
        await Assert.That(tags).DoesNotContain("ACCESS-");
        await Assert.That(tags).DoesNotContain("REFRESH-");
    }

    [Test]
    public async Task Many_parallel_logins_create_independent_sessions()
    {
        await using var app = await BffApp.StartAsync();

        var browsers = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => app.LoggedInAsync()));

        await Assert.That(browsers.Select(b => b.Cookies["__Host-TEC.Auth"]).Distinct().Count()).IsEqualTo(40);
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(40);
    }

    [Test]
    public async Task Errors_are_in_the_standard_envelope_with_a_trace_id()
    {
        await using var app = await BffApp.StartAsync();
        var browser = app.NewBrowser();

        using var response = await browser.ApiAsync(HttpMethod.Get, "/api/v1/clientes");
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("\"statusCode\":401");
        await Assert.That(body).Contains("traceId");
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
    }
}
