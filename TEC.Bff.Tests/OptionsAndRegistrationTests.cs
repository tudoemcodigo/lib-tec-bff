using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Tests.Infrastructure;
using TEC.Security.AspNetCore;
using TEC.Security.DependencyInjection;
using TEC.Security.OpenIdConnect;

namespace TEC.Bff.Tests;

/// <summary>Configuração errada ou insegura impede o host de subir (falha fechada).</summary>
public class OptionsAndRegistrationTests
{
    private static BffOptions Valid(string spaDir) => new()
    {
        ApiBaseAddress = new Uri("https://api.exemplo.com"),
        SpaRootPath = spaDir
    };

    private static string TempSpa()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tec-bff-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        return dir;
    }

    private static string[] Failures(BffOptions options) => new BffOptionsValidator().Validate(null, options).Failures?.ToArray() ?? [];

    [Test]
    public async Task Valid_options_pass()
    {
        string dir = TempSpa();
        await Assert.That(new BffOptionsValidator().Validate(null, Valid(dir)).Succeeded).IsTrue();
    }

    [Test]
    [Arguments("http://api.exemplo.com")]
    [Arguments("https://user:senha@api.exemplo.com")]
    [Arguments("https://api.exemplo.com/v1")]
    [Arguments("https://api.exemplo.com/?x=1")]
    [Arguments("ftp://api.exemplo.com")]
    public async Task Insecure_api_address_is_refused(string address)
    {
        var options = Valid(TempSpa());
        options.ApiBaseAddress = new Uri(address);

        await Assert.That(Failures(options).Any(f => f.Contains("ApiBaseAddress", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Plain_http_is_accepted_only_on_loopback()
    {
        var options = Valid(TempSpa());
        options.ApiBaseAddress = new Uri("http://localhost:5000");

        await Assert.That(Failures(options)).IsEmpty();
    }

    [Test]
    public async Task Missing_api_address_and_spa_folder_are_refused()
    {
        var options = new BffOptions();
        var failures = Failures(options);

        await Assert.That(failures.Any(f => f.Contains("ApiBaseAddress", StringComparison.Ordinal))).IsTrue();
        await Assert.That(failures.Any(f => f.Contains("SpaRootPath", StringComparison.Ordinal))).IsTrue();

        options.SpaRootPath = Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N"));
        await Assert.That(Failures(options).Any(f => f.Contains("SpaRootPath", StringComparison.Ordinal))).IsTrue();

        string semIndex = Path.Combine(Path.GetTempPath(), "sem-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(semIndex);
        options.SpaRootPath = semIndex;
        await Assert.That(Failures(options).Any(f => f.Contains("index.html", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Out_of_range_limits_are_refused()
    {
        var cases = new List<Action<BffOptions>>
        {
            o => o.MaxSessions = 0,
            o => o.MaxSessions = 2_000_000,
            o => o.SessionLifetime = TimeSpan.FromMinutes(30),
            o => o.SessionLifetime = TimeSpan.FromDays(3),
            o => o.RefreshSkew = TimeSpan.Zero,
            o => o.RefreshTimeout = TimeSpan.FromMinutes(5),
            o => o.ProxyTimeout = TimeSpan.Zero,
            o => o.ProxyTimeout = TimeSpan.FromHours(1),
            o => o.MaxRequestBodyBytes = 0,
            o => o.MaxRequestBodyBytes = 1L << 40,
            o => o.HstsMaxAge = TimeSpan.Zero
        };

        foreach (var mutate in cases)
        {
            var options = Valid(TempSpa());
            mutate(options);
            await Assert.That(Failures(options)).IsNotEmpty();
        }
    }

    [Test]
    public async Task Header_and_csp_values_cannot_inject_directives_or_headers()
    {
        var cases = new List<Action<BffOptions>>
        {
            o => o.Csp.FrameAncestors = "'none'; script-src 'unsafe-inline'",
            o => o.Csp.FrameAncestors = "",
            o => o.Csp.FormActionOrigins = ["*"],
            o => o.Csp.FormActionOrigins = ["https://*.idp.test"],
            o => o.Csp.FormActionOrigins = ["https://idp.test/"],
            o => o.Csp.FormActionOrigins = ["https://idp.test/realms/x"],
            o => o.Csp.FormActionOrigins = ["https://idp.test; script-src 'unsafe-eval'"],
            o => o.Csp.FormActionOrigins = ["https://idp.test, https://outro.test"],
            o => o.Csp.FormActionOrigins = ["javascript:alert(1)"],
            o => o.Csp.FormActionOrigins = ["data:text/html,x"],
            o => o.Csp.FormActionOrigins = ["idp.test"],
            o => o.Csp.FormActionOrigins = ["'self'"],
            o => o.Csp.FormActionOrigins = ["https://idp.test\r\nSet-Cookie: x=1"],
            o => o.Csp.ReportEndpoint = "/r; script-src 'unsafe-eval'",
            o => o.Csp.ReportEndpoint = "http://inseguro.test/relatorio",
            o => o.Csp.ReportEndpoint = "//outro.test/r",
            o => o.Csp.ReportEndpoint = "/r\r\nSet-Cookie: x=1",
            o => o.ReferrerPolicy = "no-referrer\r\nX-Injetado: 1",
            o => o.PermissionsPolicy = "camera=()\nX-Injetado: 1",
            o => o.CsrfHeaderName = "X CSRF",
            o => o.CsrfHeaderName = "",
            o => o.NoncePlaceholder = "curto",
            o => o.NoncePlaceholder = "tem espaço no meio",
            o => o.ImmutablePrefixes = ["assets/"],
            o => o.ImmutablePrefixes = ["/assets"],
            o => o.ImmutablePrefixes = ["/../"]
        };

        foreach (var mutate in cases)
        {
            var options = Valid(TempSpa());
            mutate(options);
            await Assert.That(Failures(options)).IsNotEmpty();
        }
    }

    [Test]
    [Arguments("Cookie")]
    [Arguments("authorization")]
    [Arguments("Proxy-Authorization")]
    [Arguments("Host")]
    [Arguments("Set-Cookie")]
    public async Task Credentials_can_never_be_added_to_the_forwarded_headers(string header)
    {
        var options = Valid(TempSpa());
        options.ForwardedRequestHeaders.Add(header);

        await Assert.That(Failures(options).Any(f => f.Contains("ForwardedRequestHeaders", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Host_does_not_start_with_invalid_bff_options()
    {
        await Assert.That(async () => await BffApp.StartAsync(new AppSetup { Bff = b => b.ApiBaseAddress = new Uri("http://api.publica.test") }))
            .Throws<OptionsValidationException>();
        await Assert.That(async () => await BffApp.StartAsync(new AppSetup { Bff = b => b.SpaRootPath = "Z:/nao/existe" }))
            .Throws<OptionsValidationException>();
    }

    [Test]
    public async Task Registering_the_bff_requires_the_web_login_and_only_once()
    {
        var services = new ServiceCollection();
        await Assert.That(() => services.AddTecSecurity(new ConfigurationBuilder().Build(), s => s.AddAspNetCore().AddTecBff(_ => { })))
            .Throws<InvalidOperationException>();

        var twice = new ServiceCollection();
        await Assert.That(() => twice.AddTecSecurity(new ConfigurationBuilder().Build(), s => s
                .AddAspNetCore()
                .AddOpenIdConnectWebLogin("Web", o =>
                {
                    o.Authority = FakeIdp.Authority;
                    o.ClientId = FakeIdp.ClientId;
                })
                .AddTecBff(_ => { })
                .AddTecBff(_ => { })))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Public_secrets_and_tokens_never_show_up_in_to_string()
    {
        var tokens = new TEC.Bff.Sessions.BffTokenSet("ACCESS-SEGREDO", "REFRESH-SEGREDO", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        string text = tokens + tokens.ToString() + new TEC.Bff.Tokens.TokenResult(TEC.Bff.Tokens.TokenOutcome.Ok, "ACCESS-SEGREDO")
                      + new TEC.Bff.Tokens.RefreshResult(TEC.Bff.Tokens.RefreshKind.Renewed, "ACCESS-SEGREDO", "REFRESH-SEGREDO")
                      + new TEC.Bff.Internal.TokenEndpointResponse { AccessToken = "ACCESS-SEGREDO", RefreshToken = "REFRESH-SEGREDO" };

        await Assert.That(text).DoesNotContain("SEGREDO");
    }
}
