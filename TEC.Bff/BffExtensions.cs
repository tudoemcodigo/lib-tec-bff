using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TEC.Bff.Internal;
using TEC.Bff.Login;
using TEC.Bff.Proxy;
using TEC.Bff.Sessions;
using TEC.Bff.Spa;
using TEC.Bff.Theme;
using TEC.Bff.Tokens;
using TEC.Security.AspNetCore;
using TEC.Security.DependencyInjection;

namespace TEC.Bff;

/// <summary>Registro e mapeamento do BFF.</summary>
public static class BffExtensions
{
    /// <summary>
    /// Registra o BFF sobre o login web do TEC.Security (que precisa ter sido registrado antes, por exemplo com
    /// <c>AddOpenIdConnectWebLogin</c>). Os tokens do login ficam no servidor (<see cref="IBffSessionStore"/>); o cookie
    /// <c>__Host-</c> do TEC.Security carrega só a identidade e o identificador aleatório da sessão.
    /// </summary>
    /// <exception cref="InvalidOperationException">Sem login web registrado antes. Opções inválidas impedem a subida do host.</exception>
    public static SecurityBuilder AddTecBff(this SecurityBuilder builder, Action<BffOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        string cookieScheme = builder.GetSchemes().CookieScheme
            ?? throw new InvalidOperationException("AddTecBff exige o login web do TEC.Security: registre antes, por exemplo, security.AddOpenIdConnectWebLogin(...).");
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(BffRuntime)))
            throw new InvalidOperationException("AddTecBff já foi chamado.");

        services.AddOptions<BffOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BffOptions>, BffOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBffSessionStore, InMemoryBffSessionStore>();

        var runtime = new BffRuntime(cookieScheme);
        services.AddSingleton(runtime);
        services.AddSingleton<BffSessionResolver>();
        services.AddSingleton<OidcRefreshClient>();
        services.AddSingleton<BffTokenBroker>();
        services.AddSingleton<BffProxyHandler>();
        services.AddSingleton<BffSpaHandler>();
        services.AddSingleton<ThemeStore>();
        services.AddHttpForwarder();

        // Sem redirecionamento (um 307 reenviaria credencial), sem cookie, sem proxy do sistema; o tempo limite é do encaminhador/da renovação
        services.AddHttpClient(OidcRefreshClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(BffProxyHandler.HttpClientName).ConfigurePrimaryHttpMessageHandler(CreateHandler);

        // Busca do tema: sem redirecionamento, só para endereço público (SSRF), tempo limite e teto de bytes na leitura (ThemeStore)
        services.AddHttpClient(ThemeStore.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ThemeNetwork.CreateHandler(sp.GetRequiredService<IOptions<BffOptions>>().Value.Theme.AllowPrivateNetworks))
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        BffLoginCapture.Register(services, runtime.OidcScheme);
        return builder;
    }

    /// <summary>
    /// Mapeia <c>GET /bff/login</c>, <c>POST /bff/logout</c>, <c>GET /bff/user</c>, <c>POST /bff/csp-report</c>, <c>GET /bff/theme</c> (tema sazonal) e seus assets, <c>/api/**</c> (encaminhado
    /// à API com o bearer da sessão) e o fallback que serve a SPA. Todas as rotas saem com os cabeçalhos de segurança.
    /// </summary>
    /// <remarks>
    /// Os endpoints do BFF são <c>AllowAnonymous</c> para o ASP.NET Core porque autenticam sozinhos, fechados por padrão (cookie de
    /// sessão revalidado pelo TEC.Security mais a sessão no servidor): sem isso a <c>FallbackPolicy</c> do TEC.Security responderia 401
    /// ao próprio login. Chame depois de <c>UseRouting</c> implícito do <c>WebApplication</c>; não é preciso <c>UseAuthentication</c>.
    /// </remarks>
    public static IEndpointRouteBuilder MapTecBff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<IOptions<BffOptions>>().Value;
        var headers = new BffSecurityHeaders(options);
        var runtime = services.GetRequiredService<BffRuntime>();
        var resolver = services.GetRequiredService<BffSessionResolver>();
        var store = services.GetRequiredService<IBffSessionStore>();
        var proxy = services.GetRequiredService<BffProxyHandler>();
        var spa = services.GetRequiredService<BffSpaHandler>();
        var themes = services.GetRequiredService<ThemeStore>();

        RequestDelegate Wrap(Func<HttpContext, Task> handler) => context =>
        {
            headers.Apply(context);
            return handler(context);
        };

        endpoints.MapGet(BffOptions.BasePath + "/login", Wrap(context =>
        {
            string? returnUrl = context.Request.Query["returnUrl"].Count == 1 ? context.Request.Query["returnUrl"][0] : null;
            return context.ChallengeAsync(runtime.CookieScheme, new AuthenticationProperties { RedirectUri = CsrfGuard.SafeReturnUrl(returnUrl) });
        })).AllowAnonymous();

        endpoints.MapPost(BffOptions.BasePath + "/logout", Wrap(async context =>
        {
            // Formulário de outro site não encerra a sessão do usuário (CSRF de logout): prova positiva de mesma origem
            if (!CsrfGuard.ProvenSameOrigin(context.Request))
            {
                await BffResponses.WriteErrorAsync(context, StatusCodes.Status403Forbidden, BffResponses.Csrf, "Requisição recusada.").ConfigureAwait(false);
                return;
            }

            if (await resolver.ResolveAsync(context).ConfigureAwait(false) is { } session)
                await store.RemoveAsync(session.Id, context.RequestAborted).ConfigureAwait(false);

            await context.SignOutAsync(runtime.CookieScheme).ConfigureAwait(false);
            await context.SignOutAsync(runtime.OidcScheme, new AuthenticationProperties { RedirectUri = "/" }).ConfigureAwait(false);
        })).AllowAnonymous();

        endpoints.MapGet(BffOptions.BasePath + "/user", Wrap(async context =>
        {
            if (await resolver.ResolveAsync(context).ConfigureAwait(false) is not { } session)
            {
                await BffResponses.WriteUnauthenticatedAsync(context).ConfigureAwait(false);
                return;
            }

            var body = new UserResponse(session.User.Name, [.. session.User.Permissions.Order(StringComparer.Ordinal)]);
            await BffResponses.WriteJsonAsync(context, body).ConfigureAwait(false);
        })).AllowAnonymous();

        endpoints.MapPost(BffOptions.BasePath + "/csp-report", Wrap(CspReportHandler.HandleAsync)).AllowAnonymous();

        endpoints.MapGet(BffOptions.BasePath + "/theme", Wrap(context => ThemeEndpoints.HandleThemeAsync(context, themes))).AllowAnonymous();
        endpoints.MapGet(ThemeEndpoints.AssetsPath + "/{id}/{file}", Wrap(context => ThemeEndpoints.HandleAssetAsync(context, themes))).AllowAnonymous();

        endpoints.MapMethods(BffOptions.ApiPath + "/{**path}", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], Wrap(proxy.HandleAsync)).AllowAnonymous();

        // "{*path}" (e não o padrão de MapFallback sem rota, que exclui caminhos com extensão): os arquivos do build passam por aqui também
        endpoints.MapFallback("{*path}", Wrap(spa.HandleAsync)).AllowAnonymous();
        return endpoints;
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
}

/// <summary>Cabeçalhos de segurança de toda resposta do BFF (FRT-SEG-06). A CSP vai só com o HTML da SPA.</summary>
internal sealed class BffSecurityHeaders(BffOptions options)
{
    private readonly string _hsts = $"max-age={(long)options.HstsMaxAge.TotalSeconds}; includeSubDomains";

    public void Apply(HttpContext context) =>
        context.Response.OnStarting(static state =>
        {
            var (ctx, self) = ((HttpContext, BffSecurityHeaders))state;
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = self._referrerPolicy;
            headers["Permissions-Policy"] = self._permissionsPolicy;
            if (ctx.Request.IsHttps)
                headers["Strict-Transport-Security"] = self._hsts;
            if (ctx.Request.Path.StartsWithSegments(BffOptions.BasePath, StringComparison.OrdinalIgnoreCase) && !headers.ContainsKey("Cache-Control"))
                headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }, (context, this));

    private readonly string _referrerPolicy = options.ReferrerPolicy;
    private readonly string _permissionsPolicy = options.PermissionsPolicy;
}
