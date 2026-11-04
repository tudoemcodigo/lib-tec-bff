<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="TEC.Bff" width="90" />
</div>

# ⚙️ Workflows do TEC.Bff

Os dois workflows chamam os reutilizáveis de `tudoemcodigo/tec-workflows@v1`. **Ainda não foram executados**: o repositório remoto `tudoemcodigo/lib-tec-bff` não existe e nada foi publicado.

| Workflow | Gatilhos | Permissões | O que faz |
|---|---|---|---|
| [`ci.yml`](ci.yml) | `pull_request` e `push` na `main`, `merge_group`, agendamento semanal (segunda, 06:00 UTC) e `workflow_dispatch` | `contents: read`; o job chama o reutilizável com `pull-requests: read`, `packages: write`, `actions: read`, `security-events: write`, `id-token: write` | Convenções, build e pacote, testes unitários (`TEC.Bff.Tests`, exceto a categoria `Integracao`), CodeQL; no push na `main` publica a prévia `<Version>-preview.N` |
| [`release.yml`](release.yml) | `workflow_dispatch` com a entrada `versao` (ex.: `1.0.0` ou `1.1.0-rc.1`) | `contents: write`, `packages: write`, `actions: read`, `checks: read`, `pull-requests: read`, `security-events: write`, `id-token: write`, `attestations: write` | Convenções, pacote, testes com cobertura e CodeQL; depois tag, Release e GitHub Packages |

Solução: `TEC.Bff.slnx`. Alimentador privado (`private-feed: true`).
