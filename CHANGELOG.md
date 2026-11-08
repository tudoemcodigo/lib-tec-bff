# 📝 Changelog

Todas as mudanças relevantes do **TEC.Bff** são registradas aqui. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/).

## [0.0.1] - não lançado

Versão inaugural: *Backend for Frontend* das SPAs do ecossistema TEC.

### ✨ Adicionado

- **`AddTecBff` / `MapTecBff`**: registro sobre o login web do TEC.Security e mapeamento das rotas `GET /bff/login`, `POST /bff/logout`, `GET /bff/user`, `POST /bff/csp-report`, `/api/**` e o fallback que serve a SPA.
- **Sessão no servidor**: tokens capturados pelos eventos do OIDC (sem `SaveTokens`) e guardados em `IBffSessionStore` (`InMemoryBffSessionStore`: teto de sessões, fim absoluto, thread-safe); o cookie leva só um identificador aleatório de 256 bits ([ADR 0001](docs/adr/0001-tokens-no-servidor-sem-savetokens.md)).
- **Renovação do access token** single-flight por sessão, com timeout, resposta limitada, validação do conteúdo e client secret lido do TEC.Vault a cada uso.
- **Proxy `/api/**`** (YARP `IHttpForwarder`) com bearer da sessão, lista de permissão de cabeçalhos, recusa de caminhos fora de `/api/`, teto de corpo (`MaxRequestBodyBytes`) e de tempo (`ProxyTimeout`).
- **CSRF**: cabeçalho `X-TEC-CSRF: 1` em método que muda estado, recusa de `Sec-Fetch-Site`/`Origin` de outro site, logout só da mesma origem.
- **Hospedagem da SPA**: `index.html` com CSP estrita por nonce novo a cada resposta (`Report-Only` opcional; Google Fonts opcional, desligado por padrão), `Reporting-Endpoints`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, HSTS; arquivos com cache `immutable` nos prefixos com hash; `.map`, ocultos e traversal nunca saem.
- **Telemetria**: `ActivitySource` e `Meter` `TEC.Bff`; métricas `tec.bff.login.completed`, `tec.bff.proxy.requests`, `tec.bff.token.refresh`, `tec.bff.csp.violations`; logs `EventId` 6001–6010.
- **Validação das opções na subida** (`ValidateOnStart`): URL da API HTTPS, pasta da SPA com `index.html`, limites, e valores que entram em cabeçalhos sem injeção.
- **Tema sazonal** (`GET /bff/theme` e `GET /bff/theme/assets/{id}/{file}`, opções `Theme.*`, desligado por padrão): busca o `themes.json` em URL fixa da configuração, valida com esquema fechado (os mesmos casos do pacote `@tudoemcodigo/theme`), escolhe o tema pela data de `America/Sao_Paulo`, entrega à SPA e serve os assets pela mesma origem, sem relaxar a CSP. Defesas: SSRF (só endereço público, sem redirecionamento), tempo limite e teto de bytes, assinatura das imagens, cache com `stale-if-error` e busca única por vez. Telemetria `tec.bff.theme.refresh` e logs 6011 e 6012 ([ADR 0002](docs/adr/0002-tema-sazonal-por-manifesto-fechado.md), [tema sazonal](docs/tema-sazonal.md)).
- **Login por senha** (`POST /bff/login/password`, `BffPasswordLoginOptions`, desligado por padrão): troca no IdP com o client confidencial, sessão aberta por `WebLoginSignIn` do TEC.Security, resposta única `BFF_CREDENCIAIS_INVALIDAS`, limite por usuário e por endereço (`BFF_TENTATIVAS_DEMAIS`), logs 6013 e 6014 ([ADR 0003](docs/adr/0003-login-por-senha-na-tela-da-spa.md)).
- **Dica de provedor** (`IdentityProviderHintParameter`): `GET /bff/login?idp=<alias>` vai direto ao provedor da empresa (`kc_idp_hint`), só com alias bem formado.
- Testes (feliz e adversariais) e o exemplo `samples/TEC.Bff.Sample`.

### ⚠️ Limites e itens não verificados

- Sessões em memória de **uma instância**.
- Não exercitado contra um Keycloak real; publicação Native AOT de um host não testada. Detalhes em [docs/seguranca.md](docs/seguranca.md).
