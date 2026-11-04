<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="TEC.Bff" width="90" />
</div>

# 🛡️ Segurança do TEC.Bff

Cada controle abaixo tem teste automatizado (`TEC.Bff.Tests`); a coluna "Teste" aponta a classe.

## Controles

| Ameaça | Controle | Teste |
|---|---|---|
| Roubo de token por XSS, dependência ou extensão | Tokens só no `IBffSessionStore` do servidor; nem o cookie os leva (o TEC.Security proíbe `SaveTokens`); `ToString()` dos tipos que os carregam é redigido | `LoginTests`, `OptionsAndRegistrationTests` |
| Fixação ou adivinhação de sessão | Identificador de 256 bits do gerador criptográfico, sempre gerado no servidor; o valor vive no cookie cifrado, nunca em URL ou resposta; id fornecido pelo cliente é ignorado | `LoginTests`, `SessionStoreTests` |
| Cookie roubado depois do logout | O cookie sozinho não basta: a sessão precisa existir no servidor, e o logout a apaga | `LogoutAndCspReportTests` |
| CSRF | `SameSite=Lax` (TEC.Security) + cabeçalho `X-TEC-CSRF: 1` exato em método que muda estado + recusa de `Sec-Fetch-Site`/`Origin` de outro site; logout exige prova positiva de mesma origem | `ProxyTests`, `LogoutAndCspReportTests` |
| Open redirect no login | `returnUrl` só caminho local; `//x`, `/\x`, `https://x` e `javascript:` viram `/` | `LoginTests` |
| Credencial do navegador chegando à API | Lista de permissão de cabeçalhos; `Authorization` sempre o da sessão | `ProxyTests` |
| Sair do prefixo `/api/` (`..`, `%2e`, `%2f`, `%5c`, `\`, nulo) | Recusa com 400 antes de qualquer chamada | `ProxyTests` |
| Corpo gigante / corpo em `chunked` sem tamanho | Pré-teste de `Content-Length` e leitura limitada (413) | `ProxyTests` |
| API lenta ou fora | `ProxyTimeout` explícito; 502/504 sem detalhe | `ProxyTests` |
| Renovação concorrente queimando refresh token rotativo | Single-flight por sessão | `RefreshTests` |
| Provedor de identidade fora, lento ou hostil | Timeout de renovação; resposta limitada a 64 KiB; JSON, tipo de token e tamanhos validados; falha passageira não derruba a sessão; recusa (400/401/403) a encerra | `RefreshTests` |
| Client secret vazando ou envelhecendo | Lido do TEC.Vault a cada login e a cada renovação; o cliente HTTP não segue redirecionamento nem guarda cookie | `RefreshTests` |
| XSS pelo HTML da SPA | CSP estrita: nonce novo por resposta (24 caracteres, 18 bytes aleatórios), `strict-dynamic`, sem `unsafe-inline`/`unsafe-eval`, `object-src 'none'`, `base-uri 'none'` | `SpaTests` |
| Arquivos que não podem sair | Só arquivos dentro do build, com tipo conhecido; `.map`, ocultos, outro `.html` e `/bff/**` nunca; traversal recusado | `SpaTests` |
| Injeção de cabeçalho/diretiva por configuração | Valores de CSP, `Referrer-Policy`, `Permissions-Policy`, nome do cabeçalho de CSRF e lista de cabeçalhos validados na subida | `OptionsAndRegistrationTests` |
| Vazamento em log, resposta, métrica ou atividade | Nenhum token, segredo ou identificador de sessão; rótulos de baixa cardinalidade; relatório de CSP nunca registrado | `LeakageAndTelemetryTests`, `LogoutAndCspReportTests` |

## Como os tokens ficam no servidor sem `SaveTokens`

O TEC.Security recusa a subida se `SaveTokens` estiver ligado (os tokens iriam para o cookie). O BFF não o liga. Ele encadeia dois eventos do OIDC **só de leitura**: `OnTokenValidated` separa os tokens na requisição do login e `OnTicketReceived` (que só roda depois que o TEC.Security aceitou e normalizou a identidade) os grava no armazém e põe no cookie apenas o identificador. Login recusado não deixa nada no armazém. Detalhes e alternativas descartadas: [ADR 0001](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/adr/0001-tokens-no-servidor-sem-savetokens.md).

## Tema sazonal

O tema é **dado, nunca código** (FRT-TEM-01): o BFF busca o documento em uma URL fixa da configuração e só entrega à SPA o que passou na validação estrita.

| Risco | Controle | Prova |
|---|---|---|
| SSRF pela busca do documento ou dos assets | URL só da configuração, nunca de requisição nem do documento; HTTPS; sem redirecionamento (3xx é falha); a conexão só segue para endereço público, resolvido e fixado ao conectar (loopback, rede privada, link-local de nuvem, CGNAT e as formas IPv6 que embutem IPv4 são recusados) | `ThemeNetworkAndOptionsTests`, `A_redirect_is_never_followed` |
| Documento ou asset gigante, sem fim ou lento | Tempo limite e teto de bytes em toda leitura, inclusive de corpo sem `Content-Length` | `A_hostile_origin_*` (`stream-without-end`, `declared-too-big`, `timeout`) |
| Documento adulterado (CSS, token protegido, campo extra, chave duplicada) | Esquema fechado, os mesmos casos do pacote `@tudoemcodigo/theme` | `ThemeDocumentTests` |
| Asset que não é imagem (script renomeado) | Assinatura (magic bytes) conferida; o `Content-Type` sai da extensão validada, nunca da origem; SVG proibido | `A_script_renamed_to_png_is_not_served` |
| Travessia de caminho nos assets | `id` e arquivo por padrão fechado, só o tema ativo, caminho cru com `%` recusado | `Any_asset_that_is_not_exactly_the_active_themes_is_a_404` |
| Origem fora do ar | Último documento válido por `Theme.StaleMaxAge`; depois, "sem tema"; recuo de 30 s entre buscas que falham | `When_the_origin_fails_*`, `A_failing_origin_is_not_hammered_*` |

## Limites conhecidos

> [!WARNING]
> - **Uma instância.** O armazém padrão é memória do processo: reiniciar apaga as sessões (o usuário entra de novo) e duas instâncias não as compartilham. Para escalar, implemente `IBffSessionStore` (compartilhado e cifrado em repouso) e registre-o antes de `AddTecBff`. O Data Protection das chaves do cookie também precisa ser compartilhado entre instâncias.
> - **`POST /bff/csp-report` é anônimo e sem limitador de taxa** (corpo de até 16 KiB, no máximo 20 relatórios lidos por requisição). Coloque um limitador à frente se o BFF for exposto à internet.
> - **Log do YARP.** Em falha de rede o YARP registra a exceção do .NET, cuja mensagem inclui host e porta do destino. Nenhuma credencial entra nela, mas mantenha o log com controle de acesso.
> - **Permissões em `/bff/user`** vêm da identidade do login (ID token) e servem só à interface; a API decide.
> - **Sem circuit breaker** no pedido de renovação: o limite é o timeout e o single-flight (um pedido por sessão por vez). Com muitas sessões e o provedor fora, cada sessão tenta uma vez por requisição.
> - **Logout de sessão simultânea** em outro dispositivo e revogação pelo provedor (*back-channel logout*) não são tratados: a sessão acaba quando o refresh é recusado ou o access token vence sem refresh.

## Não verificado

- Execução contra um Keycloak real (logout com `id_token_hint`, claims de roles, audiência).
- Publicação em Native AOT de um host que use o componente.
- Comportamento sob o Kestrel real do limite `MaxRequestBodySize` (o teste usa `TestServer`; o limite por `Content-Length` e pela leitura é do próprio componente).
