using System.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Bff.Sessions;
using TEC.Security.AspNetCore;
using TEC.Security.DependencyInjection;
using TEC.Security.OpenIdConnect;
using TEC.Vault.Abstractions;

namespace TEC.Bff.Tests.Infrastructure;

/// <summary>Como montar o host de teste.</summary>
internal sealed class AppSetup
{
    public Action<BffOptions>? Bff { get; set; }

    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>Com cofre, o client secret vem de <c>ClientSecretName</c> (lido a cada uso); sem cofre, das opções.</summary>
    public FakeSecretReader? Vault { get; set; }

    public bool IndexWithoutNonce { get; set; }

    public Action<FakeIdp>? Idp { get; set; }
}

/// <summary>
/// BFF completo em <c>TestServer</c> (sem rede): TEC.Security + login OpenID Connect contra o <see cref="FakeIdp"/>, API de
/// mentira atrás do proxy e uma pasta de SPA temporária. Tudo o que o navegador faria, o <see cref="Browser"/> faz.
/// </summary>
internal sealed class BffApp : IAsyncDisposable
{
    private BffApp(WebApplication app, FakeIdp idp, FakeApi api, TestLoggerProvider logs, ManualTimeProvider clock, string root)
    {
        App = app;
        Idp = idp;
        Api = api;
        Logs = logs;
        Clock = clock;
        Root = root;
        Client = app.GetTestClient();
        Client.BaseAddress = new Uri("https://localhost/");
    }

    public WebApplication App { get; }

    public FakeIdp Idp { get; }

    public FakeApi Api { get; }

    public TestLoggerProvider Logs { get; }

    public ManualTimeProvider Clock { get; }

    public HttpClient Client { get; }

    /// <summary>Pasta temporária que contém <c>dist/</c> (a SPA) e um <c>secret.txt</c> fora dela.</summary>
    public string Root { get; }

    public string SpaDir => Path.Combine(Root, "dist");

    public IBffSessionStore Store => App.Services.GetRequiredService<IBffSessionStore>();

    public Browser NewBrowser() => new(Client);

    public static async Task<BffApp> StartAsync(AppSetup? setup = null)
    {
        setup ??= new AppSetup();
        string root = Path.Combine(Path.GetTempPath(), "tec-bff-tests-" + Guid.NewGuid().ToString("N"));
        CreateSpa(root, setup.IndexWithoutNonce);

        var idp = new FakeIdp();
        setup.Idp?.Invoke(idp);
        var api = new FakeApi();
        var clock = new ManualTimeProvider();
        var logs = new TestLoggerProvider();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:RolesAsPermissions"] = "true" });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        var services = builder.Services;
        services.AddSingleton<TimeProvider>(clock);
        if (setup.Vault is not null)
            services.AddSingleton<ISecretReader>(setup.Vault);

        services.AddTecSecurity(builder.Configuration, security => security
            .AddAspNetCore()
            .AddOpenIdConnectWebLogin("Web", o =>
            {
                o.Authority = FakeIdp.Authority;
                o.ClientId = FakeIdp.ClientId;
                if (setup.Vault is null)
                    o.ClientSecret = FakeIdp.ClientSecret;
                else
                    o.ClientSecretName = "bff-client-secret";
            })
            .AddTecBff(b =>
            {
                b.ApiBaseAddress = new Uri("https://api.test");
                b.SpaRootPath = Path.Combine(root, "dist");
                setup.Bff?.Invoke(b);
            }));

        // Configure (não PostConfigure): precisa vir antes de o ASP.NET Core criar o Backchannel a partir do handler
        services.Configure<OpenIdConnectOptions>("Web.oidc", o => o.BackchannelHttpHandler = idp);
        services.AddHttpClient("TEC.Bff.Refresh").ConfigurePrimaryHttpMessageHandler(() => idp);
        services.AddHttpClient("TEC.Bff.Api").ConfigurePrimaryHttpMessageHandler(() => api);
        setup.Services?.Invoke(services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTecBff();
        await app.StartAsync().ConfigureAwait(false);
        return new BffApp(app, idp, api, logs, clock, root);
    }

    /// <summary>Faz o login completo (login, provedor, retorno) e devolve a resposta do retorno (<c>/signin-oidc</c>).</summary>
    public async Task<HttpResponseMessage> LoginAsync(Browser browser, string returnUrl = "/")
    {
        using var challenge = await browser.GetAsync("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl),
            r => r.Headers.TryAddWithoutValidation("Accept", "text/html")).ConfigureAwait(false);
        if ((int)challenge.StatusCode != 302 || challenge.Headers.Location is not { } location)
            throw new InvalidOperationException($"Login não redirecionou ao provedor: {(int)challenge.StatusCode}");

        var query = HttpUtility.ParseQueryString(location.IsAbsoluteUri ? location.Query : "?" + location.OriginalString.Split('?', 2)[1]);
        string code = "code-" + Guid.NewGuid().ToString("N");
        Idp.NonceByCode[code] = query["nonce"]!;
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });
        return await browser.SendAsync(HttpMethod.Post, "/signin-oidc", form, r =>
        {
            r.Headers.TryAddWithoutValidation("Origin", "https://idp.test");
            r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        }).ConfigureAwait(false);
    }

    /// <summary>Navegador já logado.</summary>
    public async Task<Browser> LoggedInAsync()
    {
        var browser = NewBrowser();
        using var response = await LoginAsync(browser).ConfigureAwait(false);
        if ((int)response.StatusCode != 302 || !browser.Cookies.ContainsKey("__Host-TEC.Auth"))
            throw new InvalidOperationException($"Login de teste falhou: {(int)response.StatusCode}");
        return browser;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
        Idp.Dispose();
        Api.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // pasta temporária: sobra de teste não derruba a suíte
        }
    }

    private static void CreateSpa(string root, bool withoutNonce)
    {
        string dist = Path.Combine(root, "dist");
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        string script = withoutNonce ? "<script type=\"module\" src=\"/assets/app-abc123.js\"></script>"
            : "<script type=\"module\" nonce=\"__CSP_NONCE__\" src=\"/assets/app-abc123.js\"></script>";
        File.WriteAllText(Path.Combine(dist, "index.html"),
            "<!doctype html><html lang=\"pt-BR\"><head><title>SPA</title><link rel=\"stylesheet\" href=\"/assets/app.css\"></head><body><div id=\"root\"></div>" + script + "</body></html>");
        File.WriteAllText(Path.Combine(dist, "assets", "app-abc123.js"), "console.log('app');");
        File.WriteAllText(Path.Combine(dist, "assets", "app-abc123.js.map"), "{\"version\":3}");
        File.WriteAllText(Path.Combine(dist, "assets", "app.css"), "body{margin:0}");
        File.WriteAllText(Path.Combine(dist, "favicon.ico"), "ico");
        File.WriteAllText(Path.Combine(dist, "manifest.webmanifest"), "{}");
        File.WriteAllText(Path.Combine(dist, "other.html"), "<html>outra página</html>");
        File.WriteAllText(Path.Combine(dist, ".env"), "SEGREDO=nao-pode-sair");
        File.WriteAllText(Path.Combine(root, "secret.txt"), "FORA-DA-PASTA-DO-BUILD");
    }
}
