# ADR 0001: Tokens no servidor sem `SaveTokens`: captura pelos eventos do OIDC e armazém atrás de interface

## Status
Proposto (a Doutrina só promove a regra `MUST` depois da verificação contra o Keycloak real)

## Data
2026-11-03

## Contexto
O ADR 0002 da família de frontend da Doutrina TEC exige que a SPA se autentique por um BFF: os tokens ficam no servidor e o navegador recebe só um cookie de sessão `HttpOnly`. O login web do `TEC.Security.AspNetCore` já oferece Authorization Code com PKCE e o cookie `__Host-TEC.Auth`, mas **recusa de propósito** `SaveTokens = true` e `MapInboundClaims = true` (os tokens iriam para o cookie, e o `TEC.Security` só grava no cookie a identidade normalizada). Um BFF precisa dos tokens para anexar o bearer e renová-lo.

## Decisão
- **Não alterar o TEC.Security** e **não ligar `SaveTokens`**. O BFF encadeia, por `PostConfigure<OpenIdConnectOptions>`, dois eventos do esquema `{Scheme}.oidc`, preservando os delegates que já existem (por exemplo, o do segredo do cofre):
  - `OnTokenValidated` apenas **lê** a resposta do endpoint de token e a deixa em `HttpContext.Items` (vive só até o fim da requisição do login);
  - `OnTicketReceived`, que **só roda se o TEC.Security já aceitou e normalizou a identidade**, grava os tokens no `IBffSessionStore` sob um identificador novo de 256 bits e põe no cookie apenas esse identificador (`AuthenticationProperties.Items[".tec.bff.sid"]`, dentro do cookie cifrado pelo Data Protection). Armazém cheio ou resposta sem `access_token` recusam o login (503/401) e nada é gravado.
- **Armazém atrás de `IBffSessionStore`**, com implementação em memória (`InMemoryBffSessionStore`): teto de sessões, fim absoluto, thread-safe. Serve a uma instância; escalar exige outra implementação (compartilhada e cifrada em repouso).
- **A sessão exige o cookie e a entrada no servidor.** Logout, expiração e reinício apagam a entrada; um cookie roubado depois disso não vale.
- **Renovação** por `grant_type=refresh_token` direto ao endpoint de token descoberto pelo próprio login, **single-flight por sessão**, desacoplada da requisição que a pediu, com timeout, resposta limitada a 64 KiB e validação do conteúdo. O client secret é resolvido pelo mesmo caminho do login: o das opções ou o que o evento `AuthorizationCodeReceived` do provedor injeta a partir do TEC.Vault (assim o nome do segredo não se repete na configuração do BFF e a rotação vale sem reiniciar). O cliente HTTP não segue redirecionamento nem guarda cookie.
- **Proxy** com `IHttpForwarder` do YARP (sem configuração de clusters): lista de **permissão** de cabeçalhos, bearer sempre o da sessão, `/api/` fixo, teto de corpo e de tempo.
- **CSRF**: `SameSite=Lax` do TEC.Security + cabeçalho customizado `X-TEC-CSRF: 1` + recusa de `Sec-Fetch-Site`/`Origin` de outro site; o logout exige prova positiva de mesma origem (um formulário não consegue enviar o cabeçalho).
- **HTML da SPA** servido pelo BFF com CSP estrita, nonce novo por resposta e os cabeçalhos de FRT-SEG-06.

## Consequências
### Positivas
- Nenhuma alteração no `TEC.Security`; os controles dele (PKCE, nonce, cookie `__Host-`, revalidação, duração máxima) continuam valendo.
- O impacto de um XSS cai de "roubo de credencial" para "uso da sessão durante a visita".
- Falha da renovação não derruba a sessão enquanto o token atual vale; recusa do provedor a encerra.

### Neutras / Negativas
- O BFF depende de dois detalhes de comportamento do `TEC.Security`/ASP.NET Core: o `Properties` do evento do OIDC ser o que vai ao cookie, e o evento `AuthorizationCodeReceived` do provedor preencher o `ClientSecret`. Ambos têm teste; uma mudança nesses comportamentos quebra os testes antes de quebrar produção.
- Sessão em memória: uma instância; reiniciar exige novo login.
- Mais um componente para operar, com superfície própria (sessão, CSRF, proxy).
- Não há *back-channel logout*: a revogação feita no provedor só é percebida quando o refresh é recusado ou o access token vence.

### Alternativas descartadas
- **`SaveTokens = true`**: proibido pelo `TEC.Security` (tokens no cookie).
- **Alterar o `TEC.Security`** para guardar tokens: amplia o contrato de outro componente sem necessidade; o que se precisa já é alcançável pelos eventos.
- **Token no navegador (mediador de token ou cliente puro de navegador)**: é a exceção do ADR 0002 da Doutrina, mais fraca.
- **Duende BFF**: não avaliada nesta decisão (licenciamento e custo de dependência são do dono do ecossistema).

## Referências
- Doutrina TEC: `familias/frontend/adr/0002-spa-autentica-por-bff-com-cookie-httponly.md`; FRT-SEG-01, 02, 03, 05, 06 e 08; ECO-IDP-01 a 05.
- [`SecurityBuilderWebLoginExtensions`](../../../TEC.Security/TEC.Security.AspNetCore/SecurityBuilderWebLoginExtensions.cs) (controles obrigatórios do login web).
