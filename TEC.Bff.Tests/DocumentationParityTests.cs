using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Bff.Internal;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>Paridade doc↔código (CMP-DOC-01): o que README, docs e ADR descrevem existe e vale como descrito.</summary>
public class DocumentationParityTests
{
    private static readonly Lazy<string> Root = new(FindRoot);

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "TEC.Bff.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("TEC.Bff.slnx não encontrado acima de " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine([Root.Value, .. relative]));

    [Test]
    public async Task Readme_documents_every_public_option_with_its_real_default()
    {
        string readme = Read("README.md");
        var defaults = new BffOptions();

        foreach (var property in typeof(BffOptions).GetProperties().Where(p => p.DeclaringType == typeof(BffOptions)))
        {
            if (property.Name == nameof(BffOptions.Csp))
                continue;
            await Assert.That(readme).Contains($"`{property.Name}`");
        }

        foreach (var property in typeof(BffCspOptions).GetProperties())
            await Assert.That(readme).Contains($"`Csp.{property.Name}`");

        // Tema sazonal: toda opção pública com o padrão real
        foreach (var property in typeof(BffThemeOptions).GetProperties())
            await Assert.That(readme).Contains($"`Theme.{property.Name}`");
        var theme = new BffThemeOptions();
        await Assert.That(readme).Contains($"| `Theme.CacheTtl` | {theme.CacheTtl.TotalMinutes:0} minutos");
        await Assert.That(readme).Contains($"| `Theme.FetchTimeout` | {theme.FetchTimeout.TotalSeconds:0} s");
        await Assert.That(readme).Contains($"| `Theme.StaleMaxAge` | {theme.StaleMaxAge.TotalHours:0} horas");
        await Assert.That(readme).Contains($"| `Theme.MaxDocumentBytes` | {theme.MaxDocumentBytes / 1024} KiB");
        await Assert.That(readme).Contains($"| `Theme.MaxAssetBytes` | {theme.MaxAssetBytes / 1024} KiB");

        await Assert.That(readme).Contains($"| `NoncePlaceholder` | `{defaults.NoncePlaceholder}` |");
        await Assert.That(readme).Contains($"| `CsrfHeaderName` | `{defaults.CsrfHeaderName}` (valor `1`) |");
        await Assert.That(readme).Contains($"| `MaxSessions` | {defaults.MaxSessions:N0} |".Replace(',', '.'));
        await Assert.That(readme).Contains($"| `ReferrerPolicy` | `{defaults.ReferrerPolicy}` |");
        await Assert.That(readme).Contains($"| `RefreshSkew` | {defaults.RefreshSkew.TotalSeconds:0} s |");
        await Assert.That(readme).Contains($"| `RefreshTimeout` | {defaults.RefreshTimeout.TotalSeconds:0} s |");
        await Assert.That(readme).Contains($"| `ProxyTimeout` | {defaults.ProxyTimeout.TotalSeconds:0} s |");
        await Assert.That(readme).Contains($"| `SessionLifetime` | {defaults.SessionLifetime.TotalHours:0} horas");
        foreach (string header in defaults.ForwardedRequestHeaders)
            await Assert.That(readme).Contains($"`{header}`");
    }

    [Test]
    public async Task Every_mapped_route_is_documented()
    {
        await using var app = await BffApp.StartAsync();
        string readme = Read("README.md");
        var patterns = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .ToList();

        await Assert.That(patterns.Count).IsGreaterThanOrEqualTo(6);
        foreach (string pattern in patterns.Where(p => p.StartsWith("/bff", StringComparison.Ordinal)))
            await Assert.That(readme).Contains(pattern);
        await Assert.That(patterns.Any(p => p.StartsWith("/api/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(readme).Contains("`/api/**`");
    }

    [Test]
    public async Task Public_surface_is_documented()
    {
        string readme = Read("README.md");
        string changelog = Read("CHANGELOG.md");
        var publicTypes = typeof(BffOptions).Assembly.GetExportedTypes().Select(t => t.Name).ToList();

        foreach (string name in new[] { nameof(BffOptions), nameof(IBffSessionStore), nameof(InMemoryBffSessionStore), nameof(BffTokenSet), nameof(BffDiagnostics) })
            await Assert.That(readme + changelog).Contains(name);
        await Assert.That(readme).Contains("AddTecBff");
        await Assert.That(readme).Contains("MapTecBff");
        await Assert.That(publicTypes.Contains("BffExtensions")).IsTrue();
        // Nada de tipo público novo sem decisão de contrato (CMP-EST-08): a lista é fechada
        string[] expected = ["BffCspOptions", "BffDiagnostics", "BffExtensions", "BffOptions", "BffThemeOptions", "BffTokenSet", "IBffSessionStore", "InMemoryBffSessionStore"];
        await Assert.That(publicTypes.OrderBy(n => n, StringComparer.Ordinal)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Telemetry_document_lists_every_instrument_and_event_id()
    {
        string doc = Read("docs", "observabilidade.md");

        foreach (string instrument in new[] { "tec.bff.login.completed", "tec.bff.proxy.requests", "tec.bff.token.refresh", "tec.bff.csp.violations", "tec.bff.theme.refresh" })
            await Assert.That(doc).Contains(instrument);
        await Assert.That(doc).Contains(BffDiagnostics.ActivitySourceName);

        var ids = typeof(BffLog).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Select(m => m.GetCustomAttribute<LoggerMessageAttribute>()?.EventId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();
        await Assert.That(ids.Count).IsEqualTo(12);
        await Assert.That(ids.All(id => id is >= 6001 and <= 6012)).IsTrue();   // faixa própria, sem sobreposição com os outros componentes
        foreach (int id in ids)
            await Assert.That(doc).Contains($"| {id} |");
    }

    [Test]
    public async Task Error_codes_in_the_integration_guide_match_the_code()
    {
        string doc = Read("docs", "integracao-spa.md");
        var codes = typeof(BffResponses).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.GetRawConstantValue() is string s && s.StartsWith("BFF_", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        await Assert.That(codes.Count).IsGreaterThanOrEqualTo(9);
        foreach (string code in codes)
            await Assert.That(doc).Contains($"`{code}`");
    }

    [Test]
    public async Task Sample_matches_the_readme_quick_start()
    {
        string readme = Read("README.md");
        string sample = Read("samples", "TEC.Bff.Sample", "Program.cs").Replace("\r\n", "\n").TrimEnd();

        await Assert.That(readme.Replace("\r\n", "\n")).Contains(sample);
    }

    [Test]
    public async Task Required_repository_files_exist()
    {
        foreach (string file in new[] { "LICENSE", "NOTICE", "README.md", "CHANGELOG.md", "AGENTS.md", "docs/README.md", "docs/adr/README.md", "Directory.Build.props", "Directory.Packages.props", "TEC.Bff.slnx" })
            await Assert.That(File.Exists(Path.Combine(Root.Value, file))).IsTrue();
        await Assert.That(Read("README.md")).Contains("tudoemcodigo-icone-512.png");
        foreach (string readme in Directory.EnumerateFiles(Root.Value, "README.md", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            await Assert.That(File.ReadAllText(readme)).Contains("tudoemcodigo-icone-512.png");
    }
}
