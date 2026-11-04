namespace TEC.Bff;

/// <summary>
/// Tema sazonal (FRT-TEM-05): o BFF busca o documento de temas em uma URL fixa, valida, escolhe o tema pela data e entrega à SPA em
/// <c>GET /bff/theme</c>. Desligado por padrão (sem <see cref="Url"/> a SPA recebe "sem tema" e fica no tema padrão).
/// </summary>
public sealed class BffThemeOptions
{
    /// <summary>
    /// URL do <c>themes.json</c> (por exemplo, o do GitHub Pages do repositório de temas). Os assets são buscados relativos a ela
    /// (<c>assets/&lt;id&gt;/&lt;arquivo&gt;</c>). Só HTTPS, absoluta, sem credenciais, consulta nem fragmento; vem SEMPRE da configuração,
    /// nunca de requisição nem do documento. Padrão: <c>null</c> (recurso desligado).
    /// </summary>
    public Uri? Url { get; set; }

    /// <summary>Quanto tempo o documento validado vale antes de nova busca (10 s a 1 h). Padrão: 5 minutos.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Tempo limite de cada busca (1 a 30 s). Padrão: 5 s.</summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Por quanto tempo o último documento válido continua sendo usado se a origem falhar (<c>stale-if-error</c>), de <see cref="CacheTtl"/> a 7 dias.
    /// Passado o prazo, "sem tema". Padrão: 24 horas.
    /// </summary>
    public TimeSpan StaleMaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Tamanho máximo do documento, em bytes (1 KiB a 1 MiB). Padrão: 256 KiB.</summary>
    public long MaxDocumentBytes { get; set; } = 256 * 1024;

    /// <summary>Tamanho máximo de cada asset, em bytes (1 KiB a 2 MiB). Padrão: 512 KiB.</summary>
    public long MaxAssetBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// <c>true</c> permite buscar em endereço privado ou de loopback e HTTP em loopback. Só para desenvolvimento e teste: em produção
    /// deixe <c>false</c>, que recusa destino não público (defesa contra SSRF). Padrão: <c>false</c>.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}
