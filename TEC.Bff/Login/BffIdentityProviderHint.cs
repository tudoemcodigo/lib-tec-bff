using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TEC.Bff.Login;

/// <summary>
/// Leva ao IdP o provedor escolhido em <c>GET /bff/login?idp=</c> (no Keycloak, <c>kc_idp_hint</c>), no fluxo de código com PKCE
/// de sempre. Só nome no formato de alias passa; qualquer outro é ignorado, e o IdP mostra a própria tela.
/// </summary>
internal static partial class BffIdentityProviderHint
{
    public const string PropertyKey = ".tec.bff.idp";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Alias();

    public static bool IsValid(string? value) => value is not null && Alias().IsMatch(value);

    public static void Register(IServiceCollection services, string oidcScheme) =>
        services.AddOptions<OpenIdConnectOptions>(oidcScheme).PostConfigure<IOptions<BffOptions>>((options, bff) =>
        {
            var previous = options.Events.OnRedirectToIdentityProvider;
            options.Events.OnRedirectToIdentityProvider = async context =>
            {
                await previous(context).ConfigureAwait(false);
                var parameter = bff.Value.IdentityProviderHintParameter;
                if (parameter is not null && context.Properties.Items.TryGetValue(PropertyKey, out var alias) && IsValid(alias))
                    context.ProtocolMessage.SetParameter(parameter, alias);
            };

            // Com PAR (Keycloak anuncia o endpoint), o pedido vai no corpo do push e o Keycloak não lê kc_idp_hint de lá: só para
            // a entrada com dica de provedor o pedido segue pelo redirecionamento padrão (código com PKCE de sempre)
            var previousPush = options.Events.OnPushAuthorization;
            options.Events.OnPushAuthorization = context =>
            {
                // Sem push não há autenticação do client no push: o delegate anterior (segredo do cofre) não roda, e o ASP.NET só
                // aceita uma das três ações (HandlePush, SkipPush, HandleClientAuthentication)
                if (bff.Value.IdentityProviderHintParameter is not null && context.Properties.Items.TryGetValue(PropertyKey, out var alias)
                    && IsValid(alias))
                {
                    context.SkipPush();
                    return Task.CompletedTask;
                }

                return previousPush(context);
            };
        });
}
