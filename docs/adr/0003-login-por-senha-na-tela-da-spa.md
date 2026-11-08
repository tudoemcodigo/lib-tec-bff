<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# ADR 0003: Login por senha na tela da SPA e dica de provedor para o SSO

## Status
Proposto

## Data
2026-11-07

## Contexto
O ADR SEC 0002 da Doutrina (aceito em 2026-11-07, pedido do dono para o GAP) permite tela de login própria com fluxo de senha **só** num BFF confidencial, com a senha trocada no servidor, resposta única e limite de tentativas. O mesmo pedido traz o SSO da empresa na mesma tela, indo direto ao provedor dela. Este ADR registra o que cabe ao componente.

## Decisão
1. `POST /bff/login/password`, **desligado por padrão** (`PasswordLogin.Enabled`). Corpo JSON com teto de 8 KiB, cabeçalho anti-CSRF e mesma origem (como as chamadas `/api/**`).
2. O BFF faz `grant_type=password` no endpoint de token dos metadados do próprio login, com o client confidencial e o segredo lido do cofre a cada uso (o mesmo caminho da renovação). Sem segredo, não tenta (client público nunca faz o fluxo de senha).
3. O `id_token` devolvido abre a sessão por `WebLoginSignIn.SignInWithIdTokenAsync` do TEC.Security:
   - o token é validado com o handler, as chaves, o emissor e a audiência do mesmo esquema OIDC;
   - a identidade nasce no mesmo normalizador;
   - os tokens vão para o `IBffSessionStore` antes do cookie, e se o cookie não sai, a sessão é apagada.
4. **Resposta única** `BFF_CREDENCIAIS_INVALIDAS` (401) para usuário inexistente, senha errada, conta bloqueada e identidade recusada. **Limite** por usuário (só o hash do nome fica em memória) e por endereço, em janela deslizante: acima dele, `BFF_TENTATIVAS_DEMAIS` (429) com `Retry-After`, sem chamar o IdP. O IdP mantém o próprio bloqueio.
5. A senha não vai a log, métrica, exceção, `ToString()` nem resposta. Logs 6013 e 6014 trazem só a categoria do desfecho.
6. Dica de provedor:
   - `GET /bff/login?idp=<alias>` leva o alias no parâmetro `IdentityProviderHintParameter` (`kc_idp_hint` no Keycloak), dentro do fluxo de código com PKCE de sempre;
   - só passa alias no formato `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`, e qualquer outro é ignorado;
   - sem a opção configurada, o `idp` é ignorado.

## Consequências
### Positivas
- A SPA pode ter a própria tela sem tocar em senha guardada nem em token no navegador, e o SSO da empresa sai da mesma tela.

### Neutras / Negativas
- O limite de tentativas vale por instância. Com várias instâncias, conta o bloqueio do IdP.
- O fluxo de senha está fora do OAuth 2.1. Se o IdP o retirar, o endpoint responde `BFF_INDISPONIVEL`, e a tela usa o fluxo de código.
- Precisa do TEC.Security com `WebLoginSignIn` (mesma versão do pacote).

## Referências
- Doutrina: ADR SEC 0002, SEC-IDN-01, SEC-IDN-03 e SEC-IDN-07.
- [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) §2.4.
- ADR 0001 deste componente.
