<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# ADR 0002: O BFF entrega o tema sazonal como manifesto fechado, buscado no servidor

## Status
Proposto

## Data
2026-11-04

## Contexto
O dono quer trocar a identidade visual de todos os sistemas em datas comemorativas **sem republicar as SPAs**. A decisão geral e o modelo de ameaças estão no ADR 0009 da família FRT da Doutrina (aceito em 2026-11-04). Este ADR registra o que cabe ao componente: o endpoint, a busca e as defesas. Alternativas rejeitadas: o navegador buscar o CSS ou o JSON direto da origem (exigiria abrir `style-src`, `connect-src` e `img-src` da CSP e exporia o IP do visitante a um terceiro) e servir CSS remoto (CSS é código: `url()` exfiltra e uma folha adulterada falsifica interface).

## Decisão
1. `GET /bff/theme` e `GET /bff/theme/assets/{id}/{file}`, públicos e só de leitura. Sem `Theme.Url`, respondem "sem tema".
2. O BFF busca o documento em **URL fixa da configuração**, HTTPS, sem redirecionamento, só para endereço público (defesa contra SSRF), com tempo limite e teto de bytes; valida com esquema fechado (mesmos casos do pacote `@tudoemcodigo/theme`); escolhe o tema pela data de `America/Sao_Paulo` no servidor.
3. Cache de `Theme.CacheTtl`, busca única por vez (single-flight), `stale-if-error` por `Theme.StaleMaxAge`; documento ruim nunca substitui o bom.
4. Os assets são servidos pela mesma origem, só do tema ativo, com assinatura conferida e o tipo vindo da extensão validada; SVG proibido. A CSP da SPA não muda.
5. Telemetria `tec.bff.theme.refresh` e logs 6011 e 6012, sem URL, conteúdo ou dado de usuário.

## Consequências
### Positivas
- Trocar o tema é um PR no repositório de temas; nenhuma SPA é republicada e a CSP não se relaxa.
- Origem fora do ar não derruba nada: último documento válido, depois tema padrão.

### Neutras / Negativas
- Mais uma rota pública e um cliente HTTP de saída (superfície nova, coberta por testes de SSRF, tamanho, tempo e conteúdo hostil).
- A validação existe em duas linguagens (TypeScript e C#); a paridade é provada por casos compartilhados e por um hash que acusa deriva entre as cópias.
- Sem assinatura do manifesto na v1: quem comprometer o repositório de temas altera a aparência dentro dos limites do esquema, e nada além.
- Fuso: `America/Sao_Paulo`, ou UTC−03:00 fixo se o sistema não tiver o banco de fusos (Brasil sem horário de verão desde 2019).

## Referências
ADR 0009 da família FRT (Doutrina) · [Tema sazonal](../tema-sazonal.md) · [Segurança](../seguranca.md) · Pacote `@tudoemcodigo/theme`.
