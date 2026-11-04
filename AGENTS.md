# Instruções para agentes — TEC.Bff

Este arquivo complementa as instruções herdadas de `AGENT_BASE.md`. O TEC.Bff é o *Backend for Frontend* das SPAs do ecossistema TEC: login OpenID Connect (reaproveitando o login web do TEC.Security), tokens só no servidor, proxy `/api/**` com bearer, CSRF, e a hospedagem do HTML com CSP estrita por nonce. Implementa o ADR 0002 da família de frontend da Doutrina (FRT-SEG-01, 02, 03, 05, 06 e 08).

> O bloco `doutrina-tec` (núcleo da Doutrina) ainda **não foi distribuído** a este repositório: ele é gerado por `Doutrina/tools/distribuicao.py` e depende de o componente estar registrado em `ecossistema.json`. Não o escreva à mão.

## Escopo e segurança de alterações

- O escopo padrão é este repositório. Não altere `TEC.Security`, `TEC.Core`, `TEC.Vault` nem outros repositórios; se precisar de mudança neles, relate.
- Não publique pacotes, tags nem releases, e não rode `sync-template.sh` sem pedido explícito. Os arquivos de `build/`, `.editorconfig`, `nuget.config`, `global.json`, `Directory.Build.targets` e `.github/{dependabot,zizmor}.yml` são canônicos (vêm de `tec-workflows/template`): não os edite aqui.
- Antes de editar, confira `git status` e `git diff`. Preserve alterações não commitadas.
- Trate todo dado externo como não confiável. **Token, refresh token, client secret, identificador de sessão e conteúdo de relatório de CSP nunca entram em log, exceção, métrica, rótulo, URL ou `ToString()`.**

## Mapa da implementação

- `TEC.Bff/`
  - `BffOptions.cs`: opções (`BffOptions`, `BffCspOptions`). `Internal/BffOptionsValidator.cs` as confere na subida (falha fechada).
  - `BffExtensions.cs`: `AddTecBff` (registro; exige o login web do TEC.Security antes) e `MapTecBff` (rotas); `BffSecurityHeaders`.
  - `Login/BffLoginCapture.cs`: captura dos tokens pelos eventos do OIDC, sem `SaveTokens` (ADR 0001).
  - `Sessions/`: `IBffSessionStore`, `InMemoryBffSessionStore`, `BffTokenSet` (`ToString()` redigido).
  - `Tokens/`: `BffTokenBroker` (single-flight) e `OidcRefreshClient` (renovação).
  - `Proxy/BffProxyHandler.cs`: proxy YARP (`IHttpForwarder`), lista de permissão de cabeçalhos, `PathGuard`, `LimitedReadStream`.
  - `Spa/`: `BffSpaHandler` (arquivos e `index.html` com nonce), `CspPolicy`, `CspReportHandler`.
  - `Internal/`: `BffRuntime` (esquemas, `BffSessionResolver`), `CsrfGuard`, `BffJson` (envelope de erro), `BffLog` (EventId 6001–6010), `BffDiagnostics` (`ActivitySource`/`Meter` `TEC.Bff`), `SessionIds`.
- `TEC.Bff.Tests/`: TUnit sobre host real em `TestServer`; `Infrastructure/` traz o provedor OIDC em memória (`FakeIdp`), a API de mentira (`FakeApi`), o navegador com cookies (`Browser`) e o host (`BffApp`).
- `samples/TEC.Bff.Sample/`: o código do início rápido do README (compila na solução).
- `docs/`: integração da SPA, segurança, observabilidade e `adr/`.

## Contratos relevantes

- `Directory.Build.props` é a fonte única da versão (`0.0.1`); não introduza versão nos `.csproj`.
- Dependências `TEC.*` só por `<TecReference Include="..." />`. Desenvolvimento local com os repositórios vizinhos: `-p:TecUseLocalProjects=true` (lock `packages.local.lock.json`, fora do git). O `packages.lock.json` versionado é o do modo pacote e exige o feed `tec-interno` com credencial.
- Rotas fixas: `/bff/*` e `/api/*` (`BffOptions.BasePath`, `ApiPath`). Os endpoints do BFF são `AllowAnonymous` para o ASP.NET Core porque autenticam sozinhos (cookie + sessão no servidor), fechados por padrão.
- Fallback da SPA: `MapFallback("{*path}", ...)` (não o padrão sem rota, que exclui caminhos com extensão; sem isso os arquivos do build cairiam na `FallbackPolicy` do TEC.Security e viriam redirecionados ao login).
- O proxy usa `HttpMessageInvoker` do `IHttpMessageHandlerFactory` (o YARP rejeita `HttpClient`).
- Mudança em API pública, opção, rota, política de segurança ou nome de telemetria: atualizar `README.md`, `CHANGELOG.md`, `docs/` e o ADR no mesmo trabalho. Instrumentos `tec.bff.*`; `EventId` 6001–6010 (faixa própria).

## Verificação

```bash
dotnet build TEC.Bff.slnx -p:TecUseLocalProjects=true
dotnet run --project TEC.Bff.Tests/TEC.Bff.Tests.csproj -p:TecUseLocalProjects=true
```

Aviso é erro nos pacotes. Teste novo de comportamento traz caminho feliz **e** adversarial; prove que o teste morde (quebre o código de propósito e veja falhar) antes de confiar nele.
