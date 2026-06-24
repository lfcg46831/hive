# Análise de Arquitetura — HIVE

**Data:** 2026-06-24
**Âmbito:** Avaliação da solução face à fonte de verdade (`docs/bible.html`) e ao estado atual do código.
**Tipo:** Avaliação de design (não é um ADR novo; não altera o bible).

> Nota: este documento é uma análise/parecer, não uma decisão arquitetural. Não substitui nem altera o `bible`. Se alguma conclusão aqui justificar uma decisão, deve ser promovida a ADR no `bible`.

---

## 1. O que é a solução

HIVE modela uma **organização híbrida de agentes AI e pessoas** sobre o actor model (Akka.NET, .NET 8 LTS). Os princípios estruturantes são fortes e coerentes: tudo é um ator; **posição ≠ ocupante** (a estrutura é independente de quem a ocupa, agente ou humano); comunicação **exclusivamente por mensagens** segundo um protocolo organizacional canónico; neutralidade de provider na camada de AI; supervisão/resiliência via hierarquia Akka; auditabilidade total por event sourcing; e duas dimensões tratadas como cidadãs de primeira classe que raramente o são — **latência humana** e **custo por chamada**.

A topologia desejada (§5) é um cluster com roles distintos (`agents`, `gateway`, `connectors`, `api`), `PositionActor` como entidade *sharded*, singletons para conectores inbound/cost-tracker/scheduler, persistência obrigatória e Split Brain Resolver desde a F0.

## 2. Estado real vs. visão

Esta distinção é o ponto mais importante da análise.

| Camada | Visão (bible) | Estado no código |
|--------|---------------|------------------|
| Domínio (contratos, protocolo, identidades, validação) | Protocolo canónico §9, governança, config org | **Implementado e maduro** — `Hive.Domain` rico, ~61 ficheiros de teste |
| Config organizacional (GitOps YAML) | §4.7/§4.8 | **Implementado** — modelo tipado, parser YAML, validação estrutural/unicidade/cross-ref |
| Bootstrap de runtime / cluster | §5.7/§5.10 | **Esqueleto** — actor system Akka.Cluster sobe, roles espelham config; auto-seed single-node |
| Serialização versionável | ADR-007 | **Implementado** — System.Text.Json com manifests/round-trip/snapshots |
| `PositionActor` / `AiAgentActor` / `HumanProxyActor` | §5.3–5.5 (coração do modelo) | **Não implementado** — `Hive.Actors` só tem bootstrap + serialização |
| Persistência / event sourcing | Akka.Persistence.PostgreSql obrigatório | **Não ligado** — Postgres existe no compose; sem packages de persistência nos `.csproj` |
| AI Gateway multi-provider | §6 | **Não implementado** |
| Scheduler (Quartz) | ADR-004 | **Não implementado** |
| API pública / SignalR / UI React | §5.9, F1.1 | **Não implementado** (API só expõe diagnostics) |

**Leitura:** a solução está na fase de **contratos e fundação**, não de comportamento. Está exatamente onde o roadmap a coloca (F0, US-F0-05). A auditabilidade, o event sourcing e a distribuição são hoje **promessas de design**, não capacidades exercitadas. Qualquer afirmação de "memória institucional reconstruível" ou "v1 nasce distribuída" deve ser lida como intenção, ainda por validar em runtime multi-nó.

## 3. Pontos fortes

**Disciplina de camadas verificada por testes.** `Hive.Domain` não declara *nenhuma* dependência (nem Akka, nem DI, nem hosting) e isso é **forçado por teste** (`DomainIsolationTests`). O fluxo de dependências é limpo: `Domain ← Actors ← Api/Worker`, `Infrastructure ← Api/Worker`. Um domínio verdadeiramente puro é raro e é o ativo mais valioso da base de código — protege o núcleo de regras das escolhas de framework.

**Contract-first a sério.** O protocolo de mensagens (§9) está materializado em value objects de identidade, catálogo declarativo de contratos, validadores de routing/lifecycle/estrutura e serialização versionável com snapshots. A relação testes/produção é altíssima. Isto reduz drasticamente o risco da parte mais difícil de um sistema de atores: a evolução de mensagens imutáveis.

**Separação entre supervisão técnica e comando organizacional.** Tratar a hierarquia Akka (falhas de processo) como distinta da hierarquia de comando (fluxo de trabalho) é uma decisão madura que evita um erro clássico — confundir tolerância a falhas com workflow.

**Custo e latência humana como dimensões de design.** Timeouts pensados em horas/dias, budgets por posição/unidade, cost-tracker dedicado. Poucos sistemas assumem isto desde o início.

**Governação do projeto.** Fonte de verdade única (`bible`), 7 ADRs com opções/consequências, e regras claras de onde registar o quê (`CLAUDE.md`/`AGENTS.md`). Cadência por user story rastreável no histórico git.

## 4. Riscos e tensões

**(R1) Complexidade front-loaded vs. time-to-value.** A aposta central é *distribuição e rigor de contratos já* contra *valor entregue cedo*. Cluster Sharding + singletons + split-brain + event sourcing + serialização versionável **antes** de um único agente fazer trabalho útil é muito peso de sistemas distribuídos a montante. O bible assume este custo conscientemente, e o argumento ("nunca haverá migração para cluster") é legítimo. Mas numa estratégia *interno-primeiro* com equipa pequena, o risco é gastar a F0 em andaimes e demorar a ter o primeiro *vertical slice* que prova a tese. Recomendo proteger explicitamente o marco "um agente responde a uma diretiva, ponta a ponta, num nó" como prioridade sobre o aprofundamento da maquinaria distribuída.

**(R2) Event sourcing universal cobra juros.** "Todo o estado é event-sourced" implica replay, snapshotting, versionamento de eventos e — sobretudo — **tensão com o RGPD** (apagar num journal append-only é genuinamente difícil). O bible menciona reconciliação RGPD (§10), o que é bom sinal, mas é uma das áreas onde o design ainda não foi exercitado e onde os custos costumam aparecer tarde.

**(R3) A persistência ainda não existe.** O princípio "sem estado apenas-em-memória, qualquer ator pode mover-se de nó" é central, mas hoje o sistema sobe single-node sem Akka.Persistence. Até existir recuperação por replay testada, a propriedade de resiliência é teórica. Sugiro que a ligação de persistência + um teste de recuperação (matar nó, recuperar estado de posição) seja um portão de saída da F0.

**(R4) Pontos de concentração no AI Gateway e nos singletons.** Gateway com pools/rate-limiting por provider e conectores inbound como cluster singletons concentram contenção e tornam-se SPOFs lógicos. Precisa de backpressure explícito, timeouts e política de fallback (ADR-002 aponta para isto, falta materializar). `Microsoft.Extensions.AI` era recente — vale confirmar maturidade/estabilidade da API antes de a entranhar.

**(R5) Os testes difíceis ainda não começaram.** A cobertura atual é unitária/de contrato (excelente nesse nível). Falta o caro: testes multi-nó, rebalanceamento de shards, split-brain, recuperação de persistência, passivação vs. *remember-entities* para posições com agenda. São estes que validam as decisões de §5.7.

**(R6) Superfície vs. capacidade de entrega.** O bible é ambicioso (F0→F3, editor visual, ecossistema de conectores, gestão de pessoas). Com cadência aparentemente de um contribuidor, o *bus factor* e a amplitude são o principal risco de calendário. Não é um problema de arquitetura, mas condiciona quais decisões vale a pena pagar agora.

## 5. A decisão-chave a vigiar

> **Distribuição e rigor de contratos a montante** *vs.* **tempo até ao primeiro valor demonstrável.**

A arquitetura escolheu deliberadamente o primeiro lado. É defensável e bem fundamentado. O perigo não é a decisão em si — é deixá-la consumir a F0 sem um *slice* funcional que prove a tese central (uma organização de atores onde agentes e humanos colaboram por mensagens auditáveis). Mantém o slice vertical fino como bússola.

## 6. Recomendações (não vinculativas)

1. **Definir um portão de saída da F0 orientado a comportamento**, não a contratos: um `PositionActor` persistente com um `AiAgentActor` a processar uma diretiva e a registar no audit log, recuperável após queda de nó. Hoje os contratos estão à frente do comportamento.
2. **Provar a persistência cedo** (R3): ligar Akka.Persistence.PostgreSql e um teste de recuperação antes de adicionar mais protocolo.
3. **Endereçar o RGPD vs. event sourcing** (R2) com um spike concreto (cripto-shredding / tombstones) antes que o journal cresça.
4. **Especificar backpressure e fallback do gateway** (R4) como contrato testável, não só como princípio.
5. **Adicionar um teste multi-nó mínimo** (R5) — 2 nós, sharding de posições, falha de um — para tirar a distribuição do plano teórico.
6. **Confirmar maturidade de `Microsoft.Extensions.AI`** e isolar bem o seam, para que uma troca seja barata.

## 7. Veredito

Fundação sólida, disciplinada e invulgarmente bem documentada. As decisões estruturais (domínio puro, protocolo contract-first, separação supervisão/comando, custo/latência como primeira classe) estão certas e protegidas por testes. O risco não está no que foi decidido, mas no **calendário e na ordem**: muita maquinaria distribuída e de contratos antes de comportamento que prove a tese, com a persistência e a auditabilidade ainda por exercitar. Priorizar um slice vertical funcional e provar persistência/recuperação são os passos que convertem este excelente esqueleto em sistema.
