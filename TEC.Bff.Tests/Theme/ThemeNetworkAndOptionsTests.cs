using System.Net;
using TEC.Bff.Internal;
using TEC.Bff.Theme;

namespace TEC.Bff.Tests.Theme;

/// <summary>Defesa contra SSRF (só endereço público) e validação das opções do tema (falha fechada na subida).</summary>
public class ThemeNetworkAndOptionsTests
{
    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("127.255.255.254")]
    [Arguments("10.0.0.1")]
    [Arguments("172.16.0.1")]
    [Arguments("172.31.255.255")]
    [Arguments("192.168.1.1")]
    [Arguments("169.254.169.254")]          // serviço de metadados de nuvem
    [Arguments("100.64.0.1")]               // CGNAT
    [Arguments("0.0.0.0")]
    [Arguments("192.0.0.1")]
    [Arguments("198.18.0.1")]
    [Arguments("224.0.0.1")]
    [Arguments("255.255.255.255")]
    [Arguments("::")]
    [Arguments("::1")]
    [Arguments("fc00::1")]
    [Arguments("fd12:3456::1")]
    [Arguments("fe80::1")]
    [Arguments("ff02::1")]
    [Arguments("2001:db8::1")]
    [Arguments("::ffff:127.0.0.1")]         // IPv4 mapeado em IPv6 não esconde o loopback
    [Arguments("::ffff:10.0.0.1")]
    [Arguments("::ffff:169.254.169.254")]
    [Arguments("64:ff9b::7f00:1")]          // NAT64 embutindo 127.0.0.1
    [Arguments("2002:7f00:1::")]            // 6to4 embutindo 127.0.0.1
    [Arguments("::7f00:1")]                 // IPv4-compatível
    public async Task Non_public_addresses_are_refused(string address)
    {
        await Assert.That(ThemeNetwork.IsPublic(IPAddress.Parse(address))).IsFalse();
    }

    [Test]
    [Arguments("8.8.8.8")]
    [Arguments("1.1.1.1")]
    [Arguments("185.199.108.153")]          // GitHub Pages
    [Arguments("172.15.255.255")]           // logo abaixo de 172.16/12
    [Arguments("172.32.0.1")]               // logo acima de 172.16/12
    [Arguments("100.63.255.255")]
    [Arguments("2606:50c0:8000::153")]      // GitHub Pages (IPv6)
    [Arguments("::ffff:8.8.8.8")]
    public async Task Public_addresses_are_allowed(string address)
    {
        await Assert.That(ThemeNetwork.IsPublic(IPAddress.Parse(address))).IsTrue();
    }

    [Test]
    public async Task The_production_handler_refuses_to_connect_to_a_private_address()
    {
        using var handler = ThemeNetwork.CreateHandler(allowPrivateNetworks: false);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };

        // Literal de loopback: a conexão é recusada ANTES de abrir socket (não depende de rede nem de porta)
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetAsync("http://127.0.0.1:9/themes.json"));

        await Assert.That(error!.Message).Contains("não é público");
    }

    [Test]
    public async Task The_handler_never_follows_redirects_or_uses_cookies_or_proxies()
    {
        using var handler = ThemeNetwork.CreateHandler(allowPrivateNetworks: false);

        await Assert.That(handler.AllowAutoRedirect).IsFalse();
        await Assert.That(handler.UseCookies).IsFalse();
        await Assert.That(handler.UseProxy).IsFalse();
    }

    // --- Opções ---

    private static string TempSpa()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tec-bff-theme-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        return dir;
    }

    private static string[] ThemeFailures(Action<BffThemeOptions> configure)
    {
        var options = new BffOptions { ApiBaseAddress = new Uri("https://api.exemplo.com"), SpaRootPath = TempSpa() };
        configure(options.Theme);
        return new BffOptionsValidator().Validate(null, options).Failures?.Where(f => f.StartsWith("Theme", StringComparison.Ordinal)).ToArray() ?? [];
    }

    [Test]
    public async Task The_theme_is_off_by_default_and_valid_without_a_url()
    {
        await Assert.That(ThemeFailures(_ => { })).IsEmpty();
    }

    [Test]
    public async Task A_public_https_url_is_accepted()
    {
        await Assert.That(ThemeFailures(o => o.Url = new Uri("https://tudoemcodigo.github.io/themes/themes.json"))).IsEmpty();
    }

    [Test]
    [Arguments("http://tudoemcodigo.github.io/themes/themes.json")]
    [Arguments("https://user:senha@tudoemcodigo.github.io/themes.json")]
    [Arguments("https://tudoemcodigo.github.io/themes.json?token=segredo")]
    [Arguments("https://tudoemcodigo.github.io/themes.json#x")]
    [Arguments("ftp://tudoemcodigo.github.io/themes.json")]
    [Arguments("https://localhost/themes.json")]
    [Arguments("https://127.0.0.1/themes.json")]
    [Arguments("https://169.254.169.254/latest/meta-data/")]
    [Arguments("https://10.0.0.5/themes.json")]
    [Arguments("https://[::1]/themes.json")]
    public async Task An_unsafe_url_stops_the_host(string url)
    {
        await Assert.That(ThemeFailures(o => o.Url = new Uri(url)).Any(f => f.Contains("Theme.Url", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Private_networks_and_loopback_http_are_accepted_only_when_explicitly_allowed_for_development()
    {
        await Assert.That(ThemeFailures(o =>
        {
            o.Url = new Uri("http://localhost:5001/themes.json");
            o.AllowPrivateNetworks = true;
        })).IsEmpty();
        await Assert.That(ThemeFailures(o => o.Url = new Uri("http://localhost:5001/themes.json")).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Out_of_range_limits_stop_the_host()
    {
        await Assert.That(ThemeFailures(o => o.CacheTtl = TimeSpan.FromSeconds(1)).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.CacheTtl = TimeSpan.FromHours(2)).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.FetchTimeout = TimeSpan.Zero).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.FetchTimeout = TimeSpan.FromMinutes(5)).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.StaleMaxAge = TimeSpan.FromSeconds(30)).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.StaleMaxAge = TimeSpan.FromDays(30)).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.MaxDocumentBytes = 10).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.MaxDocumentBytes = 50L * 1024 * 1024).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.MaxAssetBytes = 0).Length).IsGreaterThan(0);
        await Assert.That(ThemeFailures(o => o.MaxAssetBytes = 100L * 1024 * 1024).Length).IsGreaterThan(0);
    }
}
