namespace TEC.Bff;

/// <summary>Opções do BFF (Backend for Frontend) de uma SPA. Validadas na subida do host: configuração insegura impede a aplicação de subir.</summary>
public sealed class BffOptions
{
    /// <summary>Prefixo fixo dos endpoints do próprio BFF (<c>/bff/login</c>, <c>/bff/logout</c>, <c>/bff/user</c>, <c>/bff/csp-report</c>, <c>/bff/theme</c>).</summary>
    public const string BasePath = "/bff";

    /// <summary>Prefixo fixo das rotas encaminhadas à API.</summary>
    public const string ApiPath = "/api";

    /// <summary>
    /// URL base da API que recebe as chamadas <c>/api/**</c> (o caminho é mantido: <c>/api/v1/x</c> vira <c>{ApiBaseAddress}/api/v1/x</c>).
    /// Obrigatória, absoluta, sem credenciais, e HTTPS (HTTP só em loopback).
    /// </summary>
    public Uri? ApiBaseAddress { get; set; }

    /// <summary>Pasta com o build da SPA (<c>dist/</c>, com <c>index.html</c>). Obrigatória e existente na subida.</summary>
    public string? SpaRootPath { get; set; }

    /// <summary>
    /// Marcador do nonce no HTML (a opção <c>html.cspNonce</c> do Vite escreve <c>nonce="__CSP_NONCE__"</c>); cada ocorrência é
    /// trocada por um nonce novo a cada resposta. Padrão: <c>__CSP_NONCE__</c>.
    /// </summary>
    public string NoncePlaceholder { get; set; } = "__CSP_NONCE__";

    /// <summary>Prefixos de caminho cujos arquivos têm hash no nome e saem com <c>Cache-Control: public, max-age=31536000, immutable</c>. Padrão: <c>/assets/</c>.</summary>
    public List<string> ImmutablePrefixes { get; set; } = ["/assets/"];

    /// <summary>Máximo de sessões guardadas na memória do servidor (1 a 1.000.000). Cheio: o login falha fechado (503). Padrão: 10.000.</summary>
    public int MaxSessions { get; set; } = 10_000;

    /// <summary>
    /// Duração máxima absoluta da sessão no servidor (1 a 24 horas). Mantenha igual ao <c>MaxSessionLifetime</c> do login web do
    /// TEC.Security (padrão de 8 horas), que é quem expira o cookie. Padrão: 8 horas.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Antecedência com que o access token é renovado antes de vencer (5 s a 5 min). Padrão: 30 s.</summary>
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tempo limite do pedido de renovação ao provedor de identidade (1 s a 1 min). Padrão: 10 s.</summary>
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Tempo limite de cada chamada encaminhada à API (1 s a 5 min). Padrão: 30 s.</summary>
    public TimeSpan ProxyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tamanho máximo do corpo encaminhado à API, em bytes (1 a 16 MiB). Padrão: 1 MiB (ARQ-API-15).</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Cabeçalhos que o navegador pode ter encaminhados à API. Todo o resto (inclusive <c>Cookie</c>, <c>Authorization</c> e
    /// chaves de API) é descartado: lista de permissão, não de bloqueio.
    /// </summary>
    public List<string> ForwardedRequestHeaders { get; set; } =
        ["Accept", "Accept-Encoding", "Accept-Language", "Content-Type", "Content-Length", "If-Match", "If-None-Match", "Idempotency-Key", "X-Correlation-ID", "traceparent", "tracestate"];

    /// <summary>
    /// Cabeçalho que a SPA deve enviar em toda chamada que muda estado (<c>POST</c>, <c>PUT</c>, <c>PATCH</c>, <c>DELETE</c>) a
    /// <c>/api/**</c>, com o valor <c>1</c>. Um site de outra origem não consegue enviá-lo (exigiria CORS, que o BFF não libera).
    /// Padrão: <c>X-TEC-CSRF</c>.
    /// </summary>
    public string CsrfHeaderName { get; set; } = "X-TEC-CSRF";

    /// <summary>Valor do <c>Referrer-Policy</c>. Padrão: <c>no-referrer</c>.</summary>
    public string ReferrerPolicy { get; set; } = "no-referrer";

    /// <summary>Valor do <c>Permissions-Policy</c>. Padrão: câmera, microfone, geolocalização, pagamento e USB desligados.</summary>
    public string PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    /// <summary>Validade do HSTS (de 1 dia a 2 anos); o cabeçalho só sai em requisição HTTPS. Padrão: 1 ano.</summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Política de conteúdo (CSP) do HTML da SPA.</summary>
    public BffCspOptions Csp { get; } = new();

    /// <summary>Tema sazonal entregue à SPA em <c>GET /bff/theme</c> (FRT-TEM-05). Desligado por padrão.</summary>
    public BffThemeOptions Theme { get; } = new();

    /// <summary>
    /// Login por senha na tela da própria SPA (<c>POST /bff/login/password</c>), trocada pelo BFF no IdP com o client confidencial
    /// (ADR SEC 0002 da Doutrina). Desligado por padrão; só ligue com o ADR que autoriza o sistema.
    /// </summary>
    public BffPasswordLoginOptions PasswordLogin { get; } = new();

    /// <summary>
    /// Parâmetro de autorização que leva ao IdP o provedor escolhido em <c>GET /bff/login?idp=</c> (no Keycloak,
    /// <c>kc_idp_hint</c>: vai direto ao provedor da empresa). <c>null</c> (padrão) ignora o <c>idp</c>.
    /// </summary>
    public string? IdentityProviderHintParameter { get; set; }
}

/// <summary>Limites do login por senha. A resposta é a mesma para usuário inexistente, senha errada e conta bloqueada.</summary>
public sealed class BffPasswordLoginOptions
{
    /// <summary>Liga <c>POST /bff/login/password</c>. Padrão: desligado.</summary>
    public bool Enabled { get; set; }

    /// <summary>Tentativas por usuário dentro de <see cref="Window"/> (1 a 50). O IdP mantém o próprio bloqueio.</summary>
    public int MaxAttemptsPerUser { get; set; } = 5;

    /// <summary>Tentativas por endereço IP dentro de <see cref="Window"/> (1 a 1000).</summary>
    public int MaxAttemptsPerAddress { get; set; } = 30;

    /// <summary>Janela da contagem de tentativas (1 a 60 minutos).</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Tempo limite do pedido ao IdP (1 a 60 segundos).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Opções da CSP estrita (nonce por resposta, <c>strict-dynamic</c>) servida junto do HTML da SPA.</summary>
public sealed class BffCspOptions
{
    /// <summary>
    /// <c>true</c> envia <c>Content-Security-Policy-Report-Only</c> (só relata) em vez de impor. Comece por aqui, corrija as
    /// violações e só então desligue (FRT-SEG-05). Padrão: <c>false</c>.
    /// </summary>
    public bool ReportOnly { get; set; }

    /// <summary>
    /// Libera <c>https://fonts.googleapis.com</c> em <c>style-src</c> e <c>https://fonts.gstatic.com</c> em <c>font-src</c>.
    /// Padrão: <c>false</c>. Ligar envia o IP do visitante ao Google (LGPD) e é exceção a FRT-SEG-11: registre em ADR do projeto.
    /// </summary>
    public bool AllowGoogleFonts { get; set; }

    /// <summary>
    /// Origens acrescentadas a <c>form-action</c> (além de <c>'self'</c>). O logout (<c>POST /bff/logout</c>) responde 302 ao
    /// provedor de identidade e o Chromium aplica <c>form-action</c> também aos redirecionamentos de um formulário: sem a
    /// origem do provedor, o logout é bloqueado no navegador. Cada item é uma origem exata <c>http(s)://host[:porta]</c>,
    /// sem curinga nem caminho. Padrão: vazio.
    /// </summary>
    public List<string> FormActionOrigins { get; set; } = [];

    /// <summary>Valor de <c>frame-ancestors</c>. Padrão: <c>'none'</c>.</summary>
    public string FrameAncestors { get; set; } = "'none'";

    /// <summary>Destino dos relatórios de violação (caminho do BFF ou URL absoluta HTTPS). Padrão: <c>/bff/csp-report</c>, que conta as violações.</summary>
    public string ReportEndpoint { get; set; } = BffOptions.BasePath + "/csp-report";
}
