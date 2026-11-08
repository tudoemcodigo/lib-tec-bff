<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="TEC.Bff" width="90" />
</div>

# 🔌 Integração da SPA com o BFF

O que o código da SPA precisa saber para falar com o `TEC.Bff`. Tudo acontece na **mesma origem**: o BFF serve o HTML, recebe as chamadas e as encaminha à API.

## Fluxo

1. O navegador abre `/`: o BFF devolve o `index.html` com a CSP e um nonce novo.
2. A SPA chama `GET /bff/user`.
   - `200`: há sessão; o corpo é `{ "name": "...", "permissions": ["..."] }`.
   - `401`: sem sessão. Navegue (não use `fetch`) para `/bff/login?returnUrl=/rota-atual`; o BFF leva ao provedor e, depois do login, volta para o `returnUrl`.
3. Chamadas à API vão para `/api/...` na própria origem (o `fetch` envia o cookie por padrão em mesma origem; **não** use `credentials: 'include'` nem chame outro domínio).
4. Toda chamada que **muda estado** (`POST`, `PUT`, `PATCH`, `DELETE`) leva o cabeçalho `X-TEC-CSRF: 1` (nome configurável em `BffOptions.CsrfHeaderName`).
5. Login por senha (só com `PasswordLogin.Enabled`): a tela da SPA faz `fetch('/bff/login/password', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TEC-CSRF': '1' }, body: JSON.stringify({ username, password, returnUrl }) })`; com `200`, navegue para o `returnUrl` devolvido. Não guarde a senha em estado, storage ou log da SPA. O SSO da empresa continua sendo navegação para `/bff/login?idp=<alias>&returnUrl=...`.
6. Logout: formulário `POST /bff/logout` (a CSP `form-action` precisa listar a origem do provedor de identidade em `Csp.FormActionOrigins`: o navegador bloqueia o redirecionamento do formulário para fora de `form-action`, e o logout não termina sem isso) (ou `fetch` do mesmo site). O BFF apaga a sessão, limpa o cookie e responde `302` ao encerramento de sessão do provedor; a SPA deve seguir o redirecionamento (num `fetch`, use um formulário ou navegue para o `Location`).

```ts
// Exemplo (TypeScript): a SPA não conhece token nenhum
const resposta = await fetch('/api/v1/clientes/' + id, {
  method: 'PUT',
  headers: { 'Content-Type': 'application/json', 'X-TEC-CSRF': '1', 'If-Match': etag },
  body: JSON.stringify(dados),
});
if (resposta.status === 401) location.assign('/bff/login?returnUrl=' + encodeURIComponent(location.pathname));
```

> [!WARNING]
> Trechos em TypeScript deste documento são ilustrativos e **não** são compilados pelo CI do componente (que só compila C#).

## Cabeçalhos que chegam à API

Só os da lista `BffOptions.ForwardedRequestHeaders`: `Accept`, `Accept-Encoding`, `Accept-Language`, `Content-Type`, `Content-Length`, `If-Match`, `If-None-Match`, `Idempotency-Key`, `X-Correlation-ID`, `traceparent` e `tracestate`. `Cookie`, `Authorization`, `Proxy-Authorization`, `Host` e `Set-Cookie` não podem ser acrescentados à lista (a subida falha). O `Authorization: Bearer` é sempre o da sessão.

Da resposta da API, o BFF descarta `Set-Cookie`, `Server` e `X-Powered-By`; o resto (inclusive `ETag` e `Location`) passa.

## Erros do próprio BFF

Corpo no envelope `ApiResponse` do TEC.Core, com `traceId`, sem detalhe interno:

| Status | Código | Quando |
|---|---|---|
| 400 | `BFF_REQUISICAO_INVALIDA` | Caminho de `/api` com `..`, `%2e`, `%2f`, `%5c`, `\` ou caractere de controle |
| 401 | `BFF_NAO_AUTENTICADO` | Sem cookie, cookie inválido, sessão sumida (reinício ou expiração) ou renovação recusada pelo provedor |
| 403 | `BFF_CSRF` | Chamada que muda estado sem `X-TEC-CSRF: 1`, ou vinda de outro site (`Sec-Fetch-Site`/`Origin`) |
| 404 | `BFF_NAO_ENCONTRADO` | Arquivo inexistente, `.map`, arquivo oculto, outro `.html`, ou rota reservada `/bff/**` desconhecida |
| 405 | `BFF_METODO_NAO_PERMITIDO` | Método diferente de `GET`/`HEAD` na SPA |
| 413 | `BFF_CORPO_GRANDE` | Corpo acima de `MaxRequestBodyBytes` (por `Content-Length` ou por leitura) |
| 415 | `BFF_TIPO_NAO_SUPORTADO` | Relatório de CSP com tipo de conteúdo não aceito |
| 5xx (502 ou 504, conforme o YARP decide) | `BFF_ERRO_UPSTREAM` | A API não respondeu ou estourou `ProxyTimeout` |
| 401 | `BFF_CREDENCIAIS_INVALIDAS` | Login por senha (`POST /bff/login/password`) recusado: usuário inexistente, senha errada, conta bloqueada ou identidade recusada pelo TEC.Security; sempre a mesma resposta |
| 429 | `BFF_TENTATIVAS_DEMAIS` | Login por senha acima do limite por usuário ou por endereço; traz `Retry-After` e não chega ao IdP |
| 503 | `BFF_INDISPONIVEL` | Armazém de sessões cheio no login; ou, numa chamada `/api/**`, access token vencido com o provedor de identidade fora (só este caso traz `Retry-After: 5`) |

As respostas da **API** (400, 404, 409, 428...) passam sem alteração.

## Vite e o nonce

O `index.html` precisa trazer o marcador do nonce em cada `<script>` (e `<style>` inline, se houver). Com o Vite, ligue `html.cspNonce` em `vite.config.ts` (conforme a Doutrina, FRT-SEG-05; confira a opção na versão do Vite em uso):

```ts
export default defineConfig({ html: { cspNonce: '__CSP_NONCE__' } });
```

O valor tem de ser igual a `BffOptions.NoncePlaceholder`. Se o `index.html` não tiver o marcador, o BFF registra o aviso `6008` e a CSP bloqueará os scripts. Comece com `Csp.ReportOnly = true`, acompanhe `tec.bff.csp.violations` e só então imponha.

## Cliente no Keycloak (esperado)

> [!NOTE]
> **Não verificado contra um Keycloak real.** Os testes usam um provedor em memória. O que segue é o que o código exige; confirme no ambiente.

- Cliente **confidencial** (o BFF guarda o segredo), fluxo *Authorization Code* com PKCE `S256`; Implicit, *password grant* e *service accounts* desligados para este cliente.
- *Redirect URI*: `https://<host-do-bff>/signin-oidc` (caminho padrão do handler OIDC). *Post logout redirect URI*: `https://<host-do-bff>/signout-callback-oidc`.
- Sem *Web Origins*: o BFF não usa CORS.
- O **access token** precisa ter a audiência que a API valida (mapper de audiência) e as roles que a API converte em permissões.
- O ID token precisa trazer `sub` e as roles na claim `roles` (ou `resource_access` do próprio client): são elas que `GET /bff/user` devolve como `permissions` (o `TEC.Security` converte papéis em permissões conforme `Security:RolesAsPermissions`/`IPermissionStore`). Isso é só para a interface decidir o que mostrar; **quem autoriza é a API**.
- Segredo do cliente no TEC.Vault (`ClientSecretName`); ele é lido de novo a cada login e a cada renovação.
