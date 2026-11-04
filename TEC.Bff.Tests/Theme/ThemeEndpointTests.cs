using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TEC.Bff.Tests.Infrastructure;
using TEC.Bff.Theme;

namespace TEC.Bff.Tests.Theme;

/// <summary>Relógio que só anda quando o teste manda, com data absoluta (o tema depende do dia).</summary>
internal sealed class FixedClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
}

/// <summary>Origem de temas de mentira (o GitHub Pages): programável por caminho, conta as chamadas e pode segurar a resposta.</summary>
internal sealed class FakeThemeOrigin : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<string> Requested { get; } = new();

    /// <summary>Se definido, toda resposta espera por ele (para provar o single-flight e o tempo limite).</summary>
    public Func<CancellationToken, Task>? Hold { get; set; }

    public void Set(string path, Func<HttpResponseMessage> response) => _routes[path] = _ => Task.FromResult(response());

    public void Set(string path, Func<CancellationToken, Task<HttpResponseMessage>> response) => _routes[path] = response;

    public void Json(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Set(path, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public void Bytes(string path, byte[] body, string contentType = "application/octet-stream") =>
        Set(path, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new(contentType) } } });

    public void Remove(string path) => _routes.TryRemove(path, out _);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        string path = request.RequestUri!.AbsolutePath;
        Requested.Enqueue(request.RequestUri.ToString());
        if (Hold is not null)
            await Hold(cancellationToken).ConfigureAwait(false);
        return _routes.TryGetValue(path, out var route)
            ? await route(cancellationToken).ConfigureAwait(false)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary><c>GET /bff/theme</c> e os assets do tema, de ponta a ponta no host de teste (FRT-TEM-05/06).</summary>
public class ThemeEndpointTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 9, 9];

    private const string ThemesUrl = "https://themes.test/themes.json";

    private const string Christmas = """
        {"contract":1,"themes":[{"id":"christmas-2026","from":"2026-12-01","to":"2026-12-26",
         "tokens":{"color-accent":{"light":"#B3001B","dark":"#ff9aa6"},"color-on-accent":{"light":"#ffffff","dark":"#1a0004"}},
         "assets":{"banner":"banner.png","logo":"logo.webp"},"animation":"snow"}]}
        """;

    private static readonly DateTimeOffset InsideWindow = new(2026, 12, 10, 15, 0, 0, TimeSpan.Zero);

    private sealed record Harness(BffApp App, FakeThemeOrigin Origin, FixedClock Clock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync().ConfigureAwait(false);

        public async Task<(HttpResponseMessage Response, JsonElement Body)> GetThemeAsync()
        {
            var response = await App.Client.GetAsync("/bff/theme").ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (response, JsonDocument.Parse(text).RootElement.Clone());
        }

        public async Task<string?> ActiveIdAsync()
        {
            var (_, body) = await GetThemeAsync().ConfigureAwait(false);
            var theme = body.GetProperty("theme");
            return theme.ValueKind == JsonValueKind.Null ? null : theme.GetProperty("id").GetString();
        }
    }

    private static async Task<Harness> StartAsync(
        DateTimeOffset? now = null,
        string? document = Christmas,
        Action<BffThemeOptions>? options = null,
        bool configured = true)
    {
        var clock = new FixedClock(now ?? InsideWindow);
        var origin = new FakeThemeOrigin();
        if (document is not null)
            origin.Json("/themes.json", document);
        origin.Bytes("/assets/christmas-2026/banner.png", Png);
        origin.Bytes("/assets/christmas-2026/logo.webp", Webp);

        var app = await BffApp.StartAsync(new AppSetup
        {
            Bff = b =>
            {
                if (configured)
                    b.Theme.Url = new Uri(ThemesUrl);
                options?.Invoke(b.Theme);
            },
            Services = s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddHttpClient(ThemeStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => origin);
            }
        }).ConfigureAwait(false);
        return new Harness(app, origin, clock);
    }

    // --- O caminho feliz e o desligado ---

    [Test]
    public async Task Without_a_url_the_feature_is_off_and_the_spa_gets_no_theme()
    {
        await using var h = await StartAsync(configured: false);

        var (response, body) = await h.GetThemeAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("contract").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("theme").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(h.Origin.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task The_active_theme_is_delivered_validated_without_login_and_without_leaking_the_origin()
    {
        await using var h = await StartAsync();

        var (response, body) = await h.GetThemeAsync();
        string raw = body.GetRawText();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        var theme = body.GetProperty("theme");
        await Assert.That(theme.GetProperty("id").GetString()).IsEqualTo("christmas-2026");
        await Assert.That(theme.GetProperty("animation").GetString()).IsEqualTo("snow");
        await Assert.That(theme.GetProperty("tokens").GetProperty("color-accent").GetProperty("light").GetString()).IsEqualTo("#b3001b");
        await Assert.That(theme.GetProperty("assets").GetProperty("banner").GetString()).IsEqualTo("/bff/theme/assets/christmas-2026/banner.png");
        await Assert.That(theme.GetProperty("assets").GetProperty("logo").GetString()).IsEqualTo("/bff/theme/assets/christmas-2026/logo.webp");
        await Assert.That(raw).DoesNotContain("themes.test");
    }

    [Test]
    public async Task The_window_is_judged_in_sao_paulo_time_at_the_edges()
    {
        // 02:59Z de 1/12 ainda é 30/11 no Brasil: sem tema; 03:00Z já vale. 02:59Z de 27/12 ainda é 26/12; 03:00Z não vale mais
        await using var before = await StartAsync(new DateTimeOffset(2026, 12, 1, 2, 59, 0, TimeSpan.Zero));
        await using var start = await StartAsync(new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero));
        await using var last = await StartAsync(new DateTimeOffset(2026, 12, 27, 2, 59, 0, TimeSpan.Zero));
        await using var after = await StartAsync(new DateTimeOffset(2026, 12, 27, 3, 0, 0, TimeSpan.Zero));

        await Assert.That(await before.ActiveIdAsync()).IsNull();
        await Assert.That(await start.ActiveIdAsync()).IsEqualTo("christmas-2026");
        await Assert.That(await last.ActiveIdAsync()).IsEqualTo("christmas-2026");
        await Assert.That(await after.ActiveIdAsync()).IsNull();
    }

    // --- Cache, single-flight e falha ---

    [Test]
    public async Task The_document_is_cached_for_the_ttl_and_refetched_after_it()
    {
        await using var h = await StartAsync(options: o => o.CacheTtl = TimeSpan.FromMinutes(5));

        await h.GetThemeAsync();
        await h.GetThemeAsync();
        await Assert.That(h.Origin.Calls).IsEqualTo(1);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.GetThemeAsync();
        await Assert.That(h.Origin.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Concurrent_requests_cause_a_single_fetch()
    {
        await using var h = await StartAsync();
        var release = new TaskCompletionSource();
        h.Origin.Hold = _ => release.Task;

        var requests = Enumerable.Range(0, 20).Select(_ => h.GetThemeAsync()).ToArray();
        await Task.Delay(300);
        release.SetResult();
        var results = await Task.WhenAll(requests);

        await Assert.That(h.Origin.Calls).IsEqualTo(1);
        await Assert.That(results.All(r => r.Body.GetProperty("theme").GetProperty("id").GetString() == "christmas-2026")).IsTrue();
    }

    [Test]
    public async Task When_the_origin_fails_the_last_good_document_keeps_serving_until_the_stale_limit()
    {
        await using var h = await StartAsync(options: o =>
        {
            o.CacheTtl = TimeSpan.FromMinutes(5);
            o.StaleMaxAge = TimeSpan.FromHours(1);
        });
        await Assert.That(await h.ActiveIdAsync()).IsEqualTo("christmas-2026");

        h.Origin.Json("/themes.json", "falhou", HttpStatusCode.InternalServerError);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.That(await h.ActiveIdAsync()).IsEqualTo("christmas-2026");   // obsoleto, mas válido

        h.Clock.Advance(TimeSpan.FromMinutes(55));                                  // passou de 1 hora desde a última boa
        await Assert.That(await h.ActiveIdAsync()).IsNull();
    }

    [Test]
    public async Task The_service_recovers_by_itself_when_the_origin_comes_back()
    {
        await using var h = await StartAsync(options: o => o.CacheTtl = TimeSpan.FromMinutes(1));
        await h.GetThemeAsync();
        h.Origin.Json("/themes.json", "falhou", HttpStatusCode.BadGateway);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.GetThemeAsync();
        int callsWhileDown = h.Origin.Calls;

        h.Origin.Json("/themes.json", Christmas.Replace("christmas-2026", "recovered", StringComparison.Ordinal));
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.That(await h.ActiveIdAsync()).IsEqualTo("recovered");
        await Assert.That(h.Origin.Calls).IsGreaterThan(callsWhileDown);
    }

    [Test]
    public async Task A_failing_origin_is_not_hammered_inside_the_backoff()
    {
        await using var h = await StartAsync(document: null);

        await h.GetThemeAsync();          // 404 do documento: falha
        await h.GetThemeAsync();
        await h.GetThemeAsync();
        await Assert.That(h.Origin.Calls).IsEqualTo(1);

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await h.GetThemeAsync();
        await Assert.That(h.Origin.Calls).IsEqualTo(2);
    }

    // --- Origem hostil: nada disso derruba a página nem substitui um documento bom ---

    public static IEnumerable<string> HostileOrigins() =>
    [
        "status-404", "status-500", "redirect-302", "redirect-to-internal", "declared-too-big", "stream-without-end", "invalid-json",
        "protected-token", "css-injection", "unknown-field", "wrong-contract", "html-instead-of-json", "empty-body", "timeout"
    ];

    private static void MakeHostile(FakeThemeOrigin origin, string kind)
    {
        switch (kind)
        {
            case "status-404": origin.Json("/themes.json", "{}", HttpStatusCode.NotFound); break;
            case "status-500": origin.Json("/themes.json", "{}", HttpStatusCode.InternalServerError); break;
            case "redirect-302":
            case "redirect-to-internal":
                origin.Set("/themes.json", () =>
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri(kind == "redirect-to-internal" ? "http://169.254.169.254/latest/meta-data/" : "https://outro.test/themes.json");
                    return redirect;
                });
                break;
            case "declared-too-big":
                origin.Set("/themes.json", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[300 * 1024]) });
                break;
            case "stream-without-end":
                origin.Set("/themes.json", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) });
                break;
            case "invalid-json": origin.Json("/themes.json", "{nao-e-json"); break;
            case "protected-token": origin.Json("/themes.json", """{"contract":1,"themes":[{"id":"x","from":"2026-01-01","to":"2026-12-31","tokens":{"color-error":{"light":"#000000","dark":"#ffffff"}}}]}"""); break;
            case "css-injection": origin.Json("/themes.json", """{"contract":1,"themes":[{"id":"x","from":"2026-01-01","to":"2026-12-31","tokens":{"color-accent":{"light":"#fff;}body{display:none","dark":"#ffffff"}}}]}"""); break;
            case "unknown-field": origin.Json("/themes.json", """{"contract":1,"themes":[],"script":"alert(1)"}"""); break;
            case "wrong-contract": origin.Json("/themes.json", """{"contract":2,"themes":[]}"""); break;
            case "html-instead-of-json": origin.Json("/themes.json", "<html><script>alert(1)</script></html>"); break;
            case "empty-body": origin.Json("/themes.json", string.Empty); break;
            case "timeout":
                origin.Set("/themes.json", async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Test]
    [MethodDataSource(nameof(HostileOrigins))]
    public async Task A_hostile_origin_on_a_cold_start_means_no_theme_and_a_healthy_page(string kind)
    {
        await using var h = await StartAsync(options: o => o.FetchTimeout = TimeSpan.FromSeconds(1));
        MakeHostile(h.Origin, kind);

        var (response, body) = await h.GetThemeAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("theme").ValueKind).IsEqualTo(JsonValueKind.Null);
        // Só os eventos do componente (6011 e 6012): o log da fábrica de HttpClient do framework é outro assunto, e a URL é pública
        string ours = string.Join(Environment.NewLine, h.App.Logs.Entries.Where(e => e.EventId is 6011 or 6012).Select(e => e.Message));
        await Assert.That(ours).DoesNotContain("themes.test");
        await Assert.That(ours).DoesNotContain("169.254");
    }

    [Test]
    [MethodDataSource(nameof(HostileOrigins))]
    public async Task A_hostile_origin_never_replaces_a_good_cached_document(string kind)
    {
        await using var h = await StartAsync(options: o =>
        {
            o.CacheTtl = TimeSpan.FromMinutes(1);
            o.FetchTimeout = TimeSpan.FromSeconds(1);
        });
        await Assert.That(await h.ActiveIdAsync()).IsEqualTo("christmas-2026");

        MakeHostile(h.Origin, kind);
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.That(await h.ActiveIdAsync()).IsEqualTo("christmas-2026");
    }

    [Test]
    public async Task A_redirect_is_never_followed()
    {
        await using var h = await StartAsync(document: null);
        MakeHostile(h.Origin, "redirect-to-internal");

        await h.GetThemeAsync();

        await Assert.That(h.Origin.Requested.Any(r => r.Contains("169.254", StringComparison.Ordinal))).IsFalse();
        await Assert.That(h.Origin.Calls).IsEqualTo(1);
    }

    // --- Assets ---

    [Test]
    public async Task The_active_themes_assets_are_served_with_the_type_of_the_extension_not_of_the_origin()
    {
        await using var h = await StartAsync();
        h.Origin.Bytes("/assets/christmas-2026/banner.png", Png, "text/html");   // a origem mente o tipo

        using var banner = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");
        using var logo = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/logo.webp");

        await Assert.That(banner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(banner.Content.Headers.ContentType!.MediaType).IsEqualTo("image/png");
        await Assert.That(await banner.Content.ReadAsByteArrayAsync()).IsEquivalentTo(Png);
        await Assert.That(banner.Headers.CacheControl!.Public).IsTrue();
        await Assert.That(banner.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        await Assert.That(logo.Content.Headers.ContentType!.MediaType).IsEqualTo("image/webp");
    }

    [Test]
    public async Task Assets_are_cached_and_requested_relative_to_the_document_url()
    {
        await using var h = await StartAsync();

        using var first = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");
        using var second = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");

        await Assert.That(h.Origin.Requested.Count(r => r.EndsWith("/assets/christmas-2026/banner.png", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(h.Origin.Requested.Any(r => r == "https://themes.test/assets/christmas-2026/banner.png")).IsTrue();
    }

    [Test]
    [Arguments("/bff/theme/assets/outro-tema/banner.png")]
    [Arguments("/bff/theme/assets/christmas-2026/inexistente.png")]
    [Arguments("/bff/theme/assets/christmas-2026/..%2fthemes.json")]
    [Arguments("/bff/theme/assets/christmas-2026/%2e%2e%2fthemes.json")]
    [Arguments("/bff/theme/assets/..%2fchristmas-2026/banner.png")]
    [Arguments("/bff/theme/assets/christmas-2026/banner.svg")]
    [Arguments("/bff/theme/assets/christmas-2026/a..b.png")]
    [Arguments("/bff/theme/assets/CHRISTMAS-2026/banner.png")]
    public async Task Any_asset_that_is_not_exactly_the_active_themes_is_a_404(string path)
    {
        await using var h = await StartAsync();

        using var response = await h.App.Client.GetAsync(path);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Origin.Requested.Any(r => !r.EndsWith("/themes.json", StringComparison.Ordinal) && !r.Contains("/assets/christmas-2026/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task An_asset_of_a_theme_outside_its_window_is_not_served()
    {
        await using var h = await StartAsync(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        using var response = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_script_renamed_to_png_is_not_served()
    {
        await using var h = await StartAsync();
        h.Origin.Bytes("/assets/christmas-2026/banner.png", Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "image/png");

        using var response = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task An_oversized_asset_is_not_served()
    {
        await using var h = await StartAsync(options: o => o.MaxAssetBytes = 1024);
        h.Origin.Bytes("/assets/christmas-2026/banner.png", [.. Png, .. new byte[4096]]);

        using var response = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_missing_asset_on_the_origin_is_a_404_not_a_500()
    {
        await using var h = await StartAsync();
        h.Origin.Remove("/assets/christmas-2026/banner.png");

        using var response = await h.App.Client.GetAsync("/bff/theme/assets/christmas-2026/banner.png");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Only_get_is_allowed()
    {
        await using var h = await StartAsync();

        using var post = await h.App.Client.PostAsync("/bff/theme", new StringContent("{}"));
        using var put = await h.App.Client.PutAsync("/bff/theme/assets/christmas-2026/banner.png", new ByteArrayContent(Png));

        await Assert.That(post.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(put.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    // --- Telemetria ---

    [Test]
    public async Task Refreshes_are_counted_by_outcome_without_url_or_user_data()
    {
        var measurements = new ConcurrentQueue<(string Instrument, string Tags)>();
        using var listener = new MeterListener
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

        await using var h = await StartAsync(options: o => o.CacheTtl = TimeSpan.FromMinutes(1));
        await h.GetThemeAsync();
        h.Origin.Json("/themes.json", "x", HttpStatusCode.InternalServerError);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.GetThemeAsync();

        var theme = measurements.Where(m => m.Instrument == "tec.bff.theme.refresh").Select(m => m.Tags).ToArray();
        await Assert.That(theme).Contains("outcome=ok");
        await Assert.That(theme).Contains("outcome=obsoleto");
        await Assert.That(theme.Any(t => t.Contains("themes.test", StringComparison.Ordinal))).IsFalse();
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'a');
            return ValueTask.FromResult(buffer.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
