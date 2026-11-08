<div align="center">

<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="TEC.Bff" width="120" />

# 🧱 TEC.Bff

**Backend for Frontend para SPA: login OpenID Connect com os tokens só no servidor, cookie de sessão `HttpOnly`, proxy da API com bearer, proteção CSRF e HTML com CSP estrita por nonce.**

Login web do TEC.Security (OIDC + PKCE) · Sessão no servidor · Renovação single-flight · Proxy YARP com lista de cabeçalhos permitidos · CSRF · CSP com nonce · .NET 10

[![CI](https://github.com/tudoemcodigo/lib-tec-bff/actions/workflows/ci.yml/badge.svg)](https://github.com/tudoemcodigo/lib-tec-bff/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/README.md#-compatibilidade)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-analisadores%20sem%20avisos-512BD4)](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/README.md#-compatibilidade)
[![Versão](https://img.shields.io/badge/vers%C3%A3o-0.0.1-blue)](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/CHANGELOG.md)
[![NuGet](https://img.shields.io/badge/nuget-GitHub%20Packages-0969da?logo=github)](https://github.com/orgs/tudoemcodigo/packages)
[![SLSA](https://img.shields.io/badge/SLSA-Level%202+-0969da?logo=security)](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/README.md#️-segurança)
[![Licença: Apache 2.0](https://img.shields.io/badge/licen%C3%A7a-Apache%202.0-blue)](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/LICENSE)

[📥 Instalação](#-instalação) · [🚀 Início rápido](#-início-rápido) · [📚 Documentação](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/README.md) · [📝 Changelog](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/CHANGELOG.md)

</div>

---

## 📑 Sumário

- [✨ Por que usar](#-por-que-usar)
- [📦 Pacotes](#-pacotes)
- [🧬 Ecossistema TEC](#-ecossistema-tec)
- [📥 Instalação](#-instalação)
- [🚀 Início rápido](#-início-rápido)
- [🧭 O que tem dentro](#-o-que-tem-dentro)
- [📚 Documentação](#-documentação)
- [⚡ Compatibilidade](#-compatibilidade)
- [🛡️ Segurança](#️-segurança)
- [🧪 Testes](#-testes)
- [🤝 Contribuição](#-contribuição)
- [🏷️ Versionamento](#️-versionamento)
- [📄 Licença](#-licença)

---

## ✨ Por que usar

| SPA que guarda o token no navegador | Com o TEC.Bff |
|---|---|
| Access e refresh token em `localStorage` ou em memória: qualquer script da origem (XSS, dependência comprometida, extensão) os lê | Os tokens ficam no servidor; o navegador só tem o cookie `__Host-` `HttpOnly; Secure; SameSite=Lax` |
| A SPA chama a API direto, com CORS aberto para ela | A SPA chama `/api/**` na própria origem; o BFF anexa o bearer e **não** libera CORS |
| Qualquer cabeçalho do navegador chega à API (cookie, `Authorization`, chave de API) | Só os cabeçalhos da lista de permissão atravessam; o resto cai |
| CSP por lista de domínios, fácil de burlar | CSP estrita: `script-src` por nonce novo a cada resposta com `strict-dynamic`, `object-src 'none'`, `base-uri 'none'` |
| Renovação de token em cada aba, em paralelo, queimando o refresh token rotativo | Renovação **single-flight** por sessão |
| Cada projeto reinventa o login, o logout e o CSRF | O login é o endurecido do TEC.Security; logout só por POST da mesma origem; CSRF por cabeçalho customizado |

> [!NOTE]
> O TEC.Bff implementa o ADR 0002 da família de frontend da Doutrina TEC (`familias/frontend/adr/0002-spa-autentica-por-bff-com-cookie-httponly.md`) e as regras FRT-SEG-01, 02, 03, 05, 06 e 08.

---

## 📦 Pacotes

| Pacote | O que faz | Para quem | Depende de |
|---|---|---|---|
| `TEC.Bff` | Login, sessão no servidor, renovação, proxy `/api/**`, CSRF, CSP, hospedagem da SPA | Hosts ASP.NET Core que servem uma SPA e falam com uma API protegida por JWT | `TEC.Security.AspNetCore`, `TEC.Security.OpenIdConnect` (e, por eles, `TEC.Core` e `TEC.Vault`), `Yarp.ReverseProxy` |

---

## 🧬 Ecossistema TEC

| Componente | Relação |
|---|---|
| [TEC.Security](https://github.com/tudoemcodigo/lib-tec-security) | Fornece o login web OIDC com PKCE, o cookie `__Host-TEC.Auth` e a normalização da identidade; o BFF se apoia nele e não reimplementa nada disso |
| [TEC.Vault](https://github.com/tudoemcodigo/lib-tec-vault) | O client secret do BFF vem do cofre (`ClientSecretName`), lido a cada login e a cada renovação |
| API do serviço (ex.: TemplateTEC) | Valida o JWT como sempre; recebe o bearer do BFF |

---

## 📥 Instalação

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario-github> -p <PAT com read:packages>
dotnet add package TEC.Bff --version 0.0.1
```

O feed do GitHub Packages exige um PAT *classic* com `read:packages`, mesmo para pacote público.

---

## 🚀 Início rápido

```csharp
using TEC.Bff;
using TEC.Security.AspNetCore;
using TEC.Security.DependencyInjection;
using TEC.Security.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTecSecurity(builder.Configuration, security => security
    .AddAspNetCore()
    .AddOpenIdConnectWebLogin("Web", o =>
    {
        o.Authority = builder.Configuration["Bff:Authority"]!;
        o.ClientId = "meu-bff";
        o.ClientSecretName = "meu-bff-client-secret";   // o valor vem do TEC.Vault (um ISecretReader registrado)
    })
    .AddTecBff(bff =>
    {
        bff.ApiBaseAddress = new Uri(builder.Configuration["Bff:ApiBaseAddress"]!);
        bff.SpaRootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
        bff.Csp.ReportOnly = true;   // comece só relatando; imponha depois de zerar as violações
    }));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapTecBff();

app.Run();
```

O mesmo código compila no CI em [`samples/TEC.Bff.Sample`](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/samples/TEC.Bff.Sample/Program.cs).

> [!IMPORTANT]
> - `UseAuthentication()` é obrigatório: o retorno do provedor (`/signin-oidc`) é tratado por ele.
> - `AddTecBff` exige o login web registrado **antes** (`AddOpenIdConnectWebLogin`); sem ele, ou chamado duas vezes, lança `InvalidOperationException`.
> - Atrás de um balanceador que termina o TLS, use `UseForwardedHeaders`: o cookie é sempre `Secure` e o HSTS só sai em requisição HTTPS.
> - No Vite, ligue `html.cspNonce` (o HTML sai com `nonce="__CSP_NONCE__"`); veja [integração da SPA](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/integracao-spa.md).

---

## 🧭 O que tem dentro

| Rota | Faz |
|---|---|
| `GET /bff/login?returnUrl=/caminho` | Inicia o login no provedor; `returnUrl` só aceita caminho local (senão vai para `/`). Com `IdentityProviderHintParameter`, `&idp=<alias>` vai direto ao provedor da empresa (SSO), só com alias bem formado |
| `POST /bff/login/password` | Login por senha na tela da SPA, desligado por padrão (`PasswordLogin.Enabled`, ADR 0003): JSON `{ "username", "password", "returnUrl" }`, cabeçalho anti-CSRF e mesma origem; o BFF troca no IdP com o client confidencial e abre a mesma sessão do fluxo de código. Resposta única `BFF_CREDENCIAIS_INVALIDAS` e limite de tentativas (`BFF_TENTATIVAS_DEMAIS`) |
| `POST /bff/logout` | Apaga a sessão no servidor, limpa o cookie e redireciona ao encerramento de sessão do provedor; só da mesma origem (`Sec-Fetch-Site`/`Origin`) |
| `GET /bff/user` | `{ "name": ..., "permissions": [...] }` da sessão; nunca devolve token. 401 sem sessão |
| `POST /bff/csp-report` | Recebe os relatórios de violação da CSP; conta por diretiva (lista fechada) |
| `GET /bff/theme` | Tema sazonal ativo hoje (`{ "contract": 1, "theme": {...} }`) ou `"theme": null`; público, validado, ver [tema sazonal](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/tema-sazonal.md) |
| `GET /bff/theme/assets/{id}/{file}` | Banner ou logo do tema ativo (PNG, WebP ou AVIF), servido pela mesma origem; qualquer outro caminho é 404 |
| `/api/**` | Encaminha à `ApiBaseAddress` com `Authorization: Bearer` da sessão |
| qualquer outra | Serve a SPA: arquivos do build e, para rotas do cliente, o `index.html` com a CSP e o nonce |

| Opção (`BffOptions`) | Padrão |
|---|---|
| `ApiBaseAddress` | obrigatória (HTTPS; HTTP só em loopback; só a origem, sem caminho) |
| `SpaRootPath` | obrigatória (pasta existente com `index.html`) |
| `NoncePlaceholder` | `__CSP_NONCE__` |
| `ImmutablePrefixes` | `["/assets/"]` |
| `MaxSessions` | 10.000 |
| `SessionLifetime` | 8 horas (iguale ao `MaxSessionLifetime` do login web) |
| `RefreshSkew` | 30 s |
| `RefreshTimeout` | 10 s |
| `ProxyTimeout` | 30 s |
| `MaxRequestBodyBytes` | 1 MiB |
| `ForwardedRequestHeaders` | `Accept`, `Accept-Encoding`, `Accept-Language`, `Content-Type`, `Content-Length`, `If-Match`, `If-None-Match`, `Idempotency-Key`, `X-Correlation-ID`, `traceparent`, `tracestate` |
| `CsrfHeaderName` | `X-TEC-CSRF` (valor `1`) |
| `ReferrerPolicy` | `no-referrer` |
| `PermissionsPolicy` | câmera, microfone, geolocalização, pagamento e USB desligados |
| `HstsMaxAge` | 1 ano |
| `Csp.ReportOnly` | `false` |
| `Csp.FormActionOrigins` | vazio. Acrescenta origens a `form-action`; **inclua a origem do provedor de identidade**: o logout redireciona para ele e o navegador bloqueia o redirecionamento de formulário fora de `form-action` |
| `Csp.AllowGoogleFonts` | `false` (ligar é exceção a FRT-SEG-11 e envia o IP do visitante ao Google) |
| `Csp.FrameAncestors` | `'none'` |
| `Csp.ReportEndpoint` | `/bff/csp-report` |
| `Theme` | tema sazonal, desligado por padrão: veja as opções `Theme.*` abaixo |
| `Theme.Url` | `null` (desligado). URL HTTPS do `themes.json`; sem credencial, consulta nem fragmento; sempre da configuração |
| `Theme.CacheTtl` | 5 minutos (10 s a 1 h) |
| `Theme.FetchTimeout` | 5 s (1 a 30 s) |
| `Theme.StaleMaxAge` | 24 horas (de `CacheTtl` a 7 dias): quanto o último documento válido segue valendo se a origem cair |
| `Theme.MaxDocumentBytes` | 256 KiB (1 KiB a 1 MiB) |
| `Theme.MaxAssetBytes` | 512 KiB (1 KiB a 2 MiB) |
| `Theme.AllowPrivateNetworks` | `false`. `true` só em desenvolvimento: permite loopback, rede privada e HTTP em loopback |
| `IdentityProviderHintParameter` | `null` (ignora `idp`). No Keycloak, `kc_idp_hint` |
| `PasswordLogin` | login por senha, desligado por padrão: veja as opções `PasswordLogin.*` abaixo. Só ligue com ADR que autorize o sistema (Doutrina: ADR SEC 0002) |
| `PasswordLogin.Enabled` | `false` |
| `PasswordLogin.MaxAttemptsPerUser` | 5 (1 a 50) por janela |
| `PasswordLogin.MaxAttemptsPerAddress` | 30 (1 a 1000) por janela |
| `PasswordLogin.Window` | 5 minutos (1 a 60) |
| `PasswordLogin.Timeout` | 10 s (1 a 60 s) |

Tipos públicos: `BffOptions`, `BffCspOptions`, `BffThemeOptions`, `BffPasswordLoginOptions`, `BffExtensions`. Pontos de extensão públicos: `IBffSessionStore` (armazém de sessões; padrão `InMemoryBffSessionStore`), `BffTokenSet` e `BffDiagnostics` (nomes de telemetria).

---

## 📚 Documentação

| Tema | Documento |
|---|---|
| Como a SPA conversa com o BFF (rotas, cabeçalhos, Vite, Keycloak) | [integracao-spa.md](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/integracao-spa.md) |
| Controles de segurança, ameaças e limites | [seguranca.md](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/seguranca.md) |
| Métricas, atividades e logs | [observabilidade.md](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/observabilidade.md) |
| Decisões | [docs/adr](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/adr/README.md) |

---

## ⚡ Compatibilidade

- Alvo único `net10.0`.
- `IsAotCompatible` ligado: os analisadores de AOT e trimming não emitem aviso (o build trata aviso como erro). A **publicação** de um host em Native AOT com este componente **não foi executada**: *não verificado*.
- Contra o Keycloak real o componente **não foi exercitado** nesta versão: os testes usam um provedor OIDC em memória (descoberta, JWKS, troca de código e renovação). *Não verificado.*

---

## 🛡️ Segurança

- Tokens só no servidor; o cookie (cifrado pelo Data Protection do ASP.NET Core) leva identidade e um identificador de sessão aleatório de 256 bits, gerado no servidor.
- Falha fechada: sem sessão, sem token válido, com sessão sumida ou renovação recusada, a resposta é 401 sem detalhe; provedor fora com token ainda válido continua servindo, e vencido responde 503 com `Retry-After` sem derrubar a sessão.
- A API nunca recebe o que o navegador mandou, salvo a lista de permissão; caminhos fora de `/api/` (`..`, `%2e`, `%2f`, `%5c`, `\`) são recusados.

> [!WARNING]
> **Limites desta versão.** O armazém padrão vive na memória de **uma instância**: reiniciar apaga as sessões e duas instâncias não as compartilham. O endpoint `POST /bff/csp-report` é anônimo e sem limitador de taxa (corpo de até 16 KiB). O YARP registra a mensagem das exceções de rede do .NET (host e porta): mantenha o log com controle de acesso. Veja a lista completa em [seguranca.md](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/docs/seguranca.md).

---

## 🧪 Testes

117 testes automatizados (TUnit) sobre um host real em `TestServer`, com provedor OIDC e API de mentira em memória: feliz e adversariais (CSRF, caminhos hostis, corpo gigante, nonce repetido, sessão forjada, cookie roubado após logout, provedor fora/lento/hostil, renovação concorrente, vazamento em log, resposta e métrica).

```bash
dotnet test TEC.Bff.slnx -p:TecUseLocalProjects=true   # com os repositórios vizinhos clonados
```

---

## 🤝 Contribuição

Veja o [AGENTS.md](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/AGENTS.md). Mudança de contrato atualiza README, CHANGELOG, `docs/` e o ADR no mesmo PR.

## 🏷️ Versionamento

[SemVer](https://semver.org/lang/pt-BR/); enquanto não houver produção o componente fica em `0.0.1`.

## 📄 Licença

[Apache-2.0](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/LICENSE). Veja o [NOTICE](https://github.com/tudoemcodigo/lib-tec-bff/blob/main/NOTICE).
