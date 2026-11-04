using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TEC.Bff;

/// <summary>
/// Telemetria do BFF. Registre <see cref="ActivitySourceName"/> e <see cref="MeterName"/> no OpenTelemetry (o TEC.Observability
/// assina o prefixo <c>TEC.*</c>). Nenhum rótulo leva token, identificador de sessão, usuário ou URL: só desfechos de baixa cardinalidade.
/// </summary>
/// <remarks>
/// Instrumentos: <c>tec.bff.login.completed</c> (rótulo <c>outcome</c>: <c>ok</c>, <c>sem_token</c>, <c>armazem_cheio</c>),
/// <c>tec.bff.proxy.requests</c> (<c>outcome</c>: <c>ok</c>, <c>nao_autenticado</c>, <c>csrf</c>, <c>corpo_grande</c>,
/// <c>token_indisponivel</c>, <c>erro_upstream</c>), <c>tec.bff.token.refresh</c> (<c>outcome</c>: <c>renovado</c>,
/// <c>recusado</c>, <c>indisponivel</c>), <c>tec.bff.csp.violations</c> (<c>directive</c>) e <c>tec.bff.theme.refresh</c>
/// (<c>outcome</c>: <c>ok</c>, <c>obsoleto</c>, <c>erro</c>).
/// </remarks>
public static class BffDiagnostics
{
    /// <summary>Nome do <see cref="ActivitySource"/>.</summary>
    public const string ActivitySourceName = "TEC.Bff";

    /// <summary>Nome do <see cref="Meter"/>.</summary>
    public const string MeterName = "TEC.Bff";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName, typeof(BffDiagnostics).Assembly.GetName().Version?.ToString());
    private static readonly Counter<long> Logins = Meter.CreateCounter<long>("tec.bff.login.completed", description: "Logins concluídos ou recusados pelo BFF.");
    private static readonly Counter<long> Proxied = Meter.CreateCounter<long>("tec.bff.proxy.requests", description: "Chamadas /api/** tratadas pelo BFF.");
    private static readonly Counter<long> Refreshes = Meter.CreateCounter<long>("tec.bff.token.refresh", description: "Renovações de access token.");
    private static readonly Counter<long> CspViolations = Meter.CreateCounter<long>("tec.bff.csp.violations", description: "Violações da CSP relatadas pelos navegadores.");

    private static readonly Counter<long> ThemeRefreshes = Meter.CreateCounter<long>("tec.bff.theme.refresh", description: "Buscas do documento de temas sazonais.");

    internal static void Login(string outcome) => Logins.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    internal static void Proxy(string outcome) => Proxied.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    internal static void Refresh(string outcome) => Refreshes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    internal static void Theme(string outcome) => ThemeRefreshes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    internal static void Csp(string directive) => CspViolations.Add(1, new KeyValuePair<string, object?>("directive", directive));
}
