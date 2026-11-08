<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="TEC.Bff" width="90" />
</div>

# 📈 Observabilidade do TEC.Bff

`ActivitySource` e `Meter` da BCL, ambos chamados `TEC.Bff` (`BffDiagnostics.ActivitySourceName` e `BffDiagnostics.MeterName`). O TEC.Observability os assina pelo prefixo `TEC.*`; sem ele, registre-os no OpenTelemetry.

## Métricas

| Instrumento | Tipo | Rótulo | Valores |
|---|---|---|---|
| `tec.bff.login.completed` | contador | `outcome` | `ok`, `sem_token`, `armazem_cheio` |
| `tec.bff.proxy.requests` | contador | `outcome` | `ok`, `nao_autenticado`, `csrf`, `corpo_grande`, `token_indisponivel`, `erro_upstream` |
| `tec.bff.token.refresh` | contador | `outcome` | `renovado`, `recusado`, `indisponivel` |
| `tec.bff.csp.violations` | contador | `directive` | diretivas conhecidas da CSP, ou `outra` |
| `tec.bff.theme.refresh` | contador | `outcome` | `ok` (documento novo validado), `obsoleto` (origem fora, serviu o último válido), `erro` (sem documento válido) |

Nenhum rótulo carrega usuário, sessão, token ou URL.

## Atividades

| Nome | Quando |
|---|---|
| `TEC.Bff.Proxy` | Cada chamada `/api/**` |
| `TEC.Bff.RefreshToken` | Cada pedido de renovação ao provedor |

## Logs (`EventId` 6001 a 6014, faixa própria)

| EventId | Nível | Mensagem |
|---|---|---|
| 6001 | Information | Login concluído, sessão criada no servidor |
| 6002 | Warning | Login recusado (motivo categorizado) |
| 6003 | Information | Access token renovado |
| 6004 | Warning | Renovação recusada pelo provedor; sessão encerrada |
| 6005 | Warning | Renovação indisponível (só o tipo da falha); sessão mantida |
| 6006 | Warning | Chamada recusada pelo CSRF (método) |
| 6007 | Warning | Falha ao encaminhar para a API (só o tipo do erro) |
| 6008 | Warning | `index.html` sem o marcador de nonce |
| 6009 | Warning | Violação de CSP relatada (diretiva) |
| 6010 | Information | Cookie sem sessão no servidor (reinício ou expiração) |
| 6011 | Warning | Busca do tema sazonal falhou (só a categoria: status, tamanho, tempo, tipo da exceção ou documento inválido; nunca o conteúdo) |
| 6012 | Warning | Origem do tema indisponível; servindo o último documento válido |
| 6013 | Warning | Login por senha recusado (só a categoria: credencial recusada, tentativas demais, IdP indisponível, identidade recusada; nunca usuário nem senha) |
| 6014 | Information | Login por senha concluído, sessão criada no servidor |

> [!WARNING]
> Os logs do próprio YARP (`Yarp.ReverseProxy.*`) não são deste componente: em falha de rede trazem a mensagem da exceção do .NET (host e porta do destino).

Sem *health check* próprio: o componente não expõe nenhum.
