using System.Net;
using TEC.Bff.Sessions;
using TEC.Bff.Tests.Infrastructure;

namespace TEC.Bff.Tests;

/// <summary>
/// Login por senha na tela da SPA (ADR SEC 0002): desligado por padrão, com CSRF, resposta única para credencial errada, limite de
/// tentativas antes do IdP, a mesma sessão do fluxo de código e nenhuma senha em resposta ou log. Também a dica de provedor do SSO.
/// </summary>
public class PasswordLoginTests
{
    private const string CookieName = "__Host-TEC.Auth";

    private static Task<BffApp> Ligado(Action<BffOptions>? mais = null) =>
        BffApp.StartAsync(new AppSetup
        {
            Bff = b =>
            {
                b.PasswordLogin.Enabled = true;
                mais?.Invoke(b);
            },
        });

    private static string Corpo(string usuario, string senha, string? retorno = null) =>
        $$"""{"username":"{{usuario}}","password":"{{senha}}"{{(retorno is null ? "" : $",\"returnUrl\":\"{retorno}\"")}}}""";

    private static Task<HttpResponseMessage> Entrar(Browser navegador, string corpo, bool csrf = true, Action<HttpRequestMessage>? ajuste = null) =>
        navegador.ApiAsync(HttpMethod.Post, "/bff/login/password", Browser.Json(corpo), csrf, ajuste);

    [Test]
    public async Task Desligado_PorPadrao_Responde404_SemChamarOIdp()
    {
        await using var app = await BffApp.StartAsync();

        using var resposta = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue));

        await Assert.That(resposta.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(app.Idp.PasswordCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SenhaCerta_AbreAMesmaSessaoDoFluxoDeCodigo_ComTokensSoNoServidor()
    {
        await using var app = await Ligado();
        var navegador = app.NewBrowser();

        using var resposta = await Entrar(navegador, Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue, "/painel?aba=2"));
        string corpo = await resposta.Content.ReadAsStringAsync();
        using var usuario = await navegador.ApiAsync(HttpMethod.Get, "/bff/user");

        await Assert.That(resposta.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(corpo).Contains("\"returnUrl\":\"/painel?aba=2\"");
        await Assert.That(navegador.Cookies.ContainsKey(CookieName)).IsTrue();
        await Assert.That(usuario.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await usuario.Content.ReadAsStringAsync()).Contains("Maria Teste");
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(1);
        // Nada de token nem de senha na resposta, nos cookies ou nos logs
        string tudo = corpo + string.Join('\n', navegador.SetCookieHeaders) + app.Logs.All;
        await Assert.That(tudo).DoesNotContain("ACCESS-");
        await Assert.That(tudo).DoesNotContain("REFRESH-");
        await Assert.That(tudo).DoesNotContain(FakeIdp.PasswordValue);
        await Assert.That(app.Idp.ReceivedClientSecrets).Contains(FakeIdp.ClientSecret);
    }

    [Test]
    [Arguments("maria", "senha-errada")]
    [Arguments("quem-nao-existe", "qualquer")]
    public async Task UsuarioInexistenteESenhaErrada_TemAMesmaResposta_ESemCookie(string usuario, string senha)
    {
        await using var app = await Ligado();
        var navegador = app.NewBrowser();

        using var resposta = await Entrar(navegador, Corpo(usuario, senha));
        string corpo = await resposta.Content.ReadAsStringAsync();

        await Assert.That(resposta.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(corpo).Contains("BFF_CREDENCIAIS_INVALIDAS");
        await Assert.That(corpo).DoesNotContain("Invalid user credentials");
        await Assert.That(navegador.Cookies.ContainsKey(CookieName)).IsFalse();
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
        await Assert.That(app.Logs.All).DoesNotContain(senha);
    }

    [Test]
    public async Task SemCabecalhoAntiCsrf_OuDeOutroSite_ERecusado_SemChamarOIdp()
    {
        await using var app = await Ligado();

        using var semCabecalho = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue), csrf: false);
        using var deOutroSite = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue), ajuste: r =>
        {
            r.Headers.Remove("Sec-Fetch-Site");
            r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        });

        await Assert.That(semCabecalho.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(deOutroSite.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(app.Idp.PasswordCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("{nao-e-json")]
    [Arguments("""{"username":"","password":"x"}""")]
    [Arguments("""{"username":"maria"}""")]
    [Arguments("""{"username":"ma\u0000ria","password":"x"}""")]
    public async Task CorpoInvalido_Da400_SemChamarOIdp(string corpo)
    {
        await using var app = await Ligado();

        using var resposta = await Entrar(app.NewBrowser(), corpo);

        await Assert.That(resposta.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(app.Idp.PasswordCalls).IsEqualTo(0);
    }

    [Test]
    public async Task CorpoGrandeDemais_OuQueNaoEJson_ERecusado()
    {
        await using var app = await Ligado();

        using var grande = await Entrar(app.NewBrowser(), Corpo("maria", new string('x', 9000)));
        using var texto = await app.NewBrowser().ApiAsync(HttpMethod.Post, "/bff/login/password",
            new StringContent("username=maria&password=x", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));

        await Assert.That(grande.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(texto.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        await Assert.That(app.Idp.PasswordCalls).IsEqualTo(0);
    }

    [Test]
    public async Task RetornoParaOutroSite_VoltaARaiz()
    {
        await using var app = await Ligado();

        using var resposta = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue, "https://atacante.example/"));

        await Assert.That(await resposta.Content.ReadAsStringAsync()).Contains("\"returnUrl\":\"/\"");
    }

    [Test]
    public async Task TentativasDemaisPorUsuario_Dao429_SemChamarOIdp_EOSucessoZeraAContagem()
    {
        await using var app = await Ligado(b => b.PasswordLogin.MaxAttemptsPerUser = 3);

        for (int i = 0; i < 3; i++)
        {
            using var errada = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, "errada-" + i));
            await Assert.That(errada.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var bloqueada = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue));
        int chamadasAntes = app.Idp.PasswordCalls;
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        using var depoisDaJanela = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue));
        using var outraErrada = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, "errada-de-novo"));

        await Assert.That(bloqueada.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(bloqueada.Headers.Contains("Retry-After")).IsTrue();
        await Assert.That(chamadasAntes).IsEqualTo(3);
        await Assert.That(depoisDaJanela.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(outraErrada.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task TentativasDemaisPorEndereco_CortamAVarreduraDeUsuarios()
    {
        await using var app = await Ligado(b =>
        {
            b.PasswordLogin.MaxAttemptsPerAddress = 2;
            b.PasswordLogin.MaxAttemptsPerUser = 50;
        });

        using var a = await Entrar(app.NewBrowser(), Corpo("ana", "x"));
        using var b = await Entrar(app.NewBrowser(), Corpo("bruno", "x"));
        using var c = await Entrar(app.NewBrowser(), Corpo("carla", "x"));

        await Assert.That(c.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(app.Idp.PasswordCalls).IsEqualTo(2);
    }

    [Test]
    public async Task IdpForaOuSemIdToken_Da503_ENenhumaSessao()
    {
        await using var app = await Ligado();
        app.Idp.PasswordBehavior = (_, _) => Task.FromResult(FakeIdp.Json("{}", HttpStatusCode.ServiceUnavailable));
        using var fora = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue));
        app.Idp.PasswordBehavior = (_, _) => Task.FromResult(FakeIdp.Json(app.Idp.TokenBody(includeIdToken: false)));
        using var semIdToken = await Entrar(app.NewBrowser(), Corpo(FakeIdp.PasswordUser, FakeIdp.PasswordValue));

        await Assert.That(fora.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(semIdToken.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(((InMemoryBffSessionStore)app.Store).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DicaDeProvedor_VaiAoIdpSoComAliasValido_ESoComOParametroConfigurado()
    {
        await using var ligado = await BffApp.StartAsync(new AppSetup { Bff = b => b.IdentityProviderHintParameter = "kc_idp_hint" });
        await using var desligado = await BffApp.StartAsync();

        static async Task<string> Local(BffApp app, string idp)
        {
            using var resposta = await app.NewBrowser().GetAsync("/bff/login?returnUrl=%2F&idp=" + Uri.EscapeDataString(idp),
                r => r.Headers.TryAddWithoutValidation("Accept", "text/html"));
            return resposta.Headers.Location?.ToString() ?? string.Empty;
        }

        await Assert.That(await Local(ligado, "empresa-acme")).Contains("kc_idp_hint=empresa-acme");
        await Assert.That(await Local(ligado, "a b")).DoesNotContain("kc_idp_hint");
        await Assert.That(await Local(ligado, "../../x")).DoesNotContain("kc_idp_hint");
        await Assert.That(await Local(desligado, "empresa-acme")).DoesNotContain("kc_idp_hint");
    }

    [Test]
    public async Task ComPar_ADicaDeProvedorVaiNaQuery_SemPush_EOLoginComumContinuaComPar()
    {
        await using var app = await BffApp.StartAsync(new AppSetup
        {
            Bff = b => b.IdentityProviderHintParameter = "kc_idp_hint",
            Idp = idp => idp.SupportsPar = true,
            // Como no GAP: segredo do cofre, cujo delegate trata a autenticação do client no push
            Vault = new FakeSecretReader(FakeIdp.ClientSecret),
        });

        static async Task<string> Local(BffApp app, string query)
        {
            using var resposta = await app.NewBrowser().GetAsync("/bff/login?returnUrl=%2F" + query,
                r => r.Headers.TryAddWithoutValidation("Accept", "text/html"));
            return resposta.Headers.Location?.ToString() ?? string.Empty;
        }

        string comum = await Local(app, string.Empty);
        int pushes = app.Idp.PushedRequests.Count;
        string comDica = await Local(app, "&idp=empresa-acme");

        await Assert.That(comum).Contains("request_uri=");
        await Assert.That(pushes).IsEqualTo(1);
        await Assert.That(comDica).Contains("kc_idp_hint=empresa-acme");
        await Assert.That(comDica).Contains("code_challenge=");
        await Assert.That(comDica).DoesNotContain("request_uri=");
        await Assert.That(app.Idp.PushedRequests.Count).IsEqualTo(1);
    }
}
