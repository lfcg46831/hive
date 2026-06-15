# HIVE — Organização Híbrida de Agentes AI e Pessoas sobre Actor Model

**Documento Bíblia da Solução**
**Versão:** 0.3 (rascunho evolutivo)
**Data:** 2026-06-15
**Estado:** Em elaboração incremental
**Nota:** O nome "HIVE" é um placeholder de trabalho — a definir.

---

## Histórico de Versões

| Versão | Data | Alterações |
|--------|------|-----------|
| 0.1 | 2026-06-12 | Estrutura inicial, conceitos fundamentais, arquitetura de alto nível, questões em aberto |
| 0.2 | 2026-06-12 | Resolução de Q1/Q2/Q6: estratégia interno→produto, clustering desde o início, modelo de execução híbrido (reativo + proativo). ADR-001 detalhado e aceite. Novo §1.3, §4.5, §5.6, §5.7 |
| 0.3 | 2026-06-15 | Resolução de Q4/Q5/Q7/Q8/Q9/Q10: primeiro cliente interno Engenharia/Delivery, hosting híbrido Docker Compose→Kubernetes, `Microsoft.Extensions.AI`, PostgreSQL com vector store futuro, GitOps/YAML, contratos inter-unidades e baseline europeu de RGPD. ADR-002 e ADR-003 aceites. Correção da separação entre árvore organizacional lógica e supervisão técnica Akka |

---

## 1. Visão

Criar uma plataforma que permita modelar uma **estrutura empresarial completa** — departamentos, equipas, cadeias de comando — onde os "colaboradores" são uma mistura de **agentes de AI** e **pessoas reais**, todos representados como atores num sistema baseado no actor model (Akka.NET).

A organização funciona como uma empresa real:

- **Comunicação vertical** — ordens descem na hierarquia, relatórios e escalamentos sobem (paradigma de comando).
- **Comunicação horizontal** — colegas da mesma equipa colaboram diretamente entre pares.
- **Conectores com o mundo real** — email, mensagens, APIs, documentos — permitem que pessoas reais participem na estrutura e que os agentes atuem sobre sistemas externos.

Cada agente AI é um ator configurado individualmente: provider de AI, modelo, temperatura, modo de processamento (interativo vs. batch), e uma **prompt de identidade** que define a sua persona, função, competências e limites.

### 1.1 Problema que resolve

As soluções multi-agente atuais (frameworks tipo AutoGen, CrewAI, LangGraph) tratam agentes como pipelines ou grafos de execução efémeros. Falta-lhes:

1. **Estrutura organizacional persistente** — agentes com posição, responsabilidade e relações hierárquicas estáveis no tempo.
2. **Integração de humanos como cidadãos de primeira classe** — não apenas "human-in-the-loop" de aprovação, mas pessoas que ocupam posições na estrutura, recebem e enviam mensagens como qualquer outro ator.
3. **Robustez operacional** — supervisão, recuperação de falhas, isolamento de estado, escalabilidade — exatamente o que o actor model oferece nativamente.
4. **Neutralidade de provider** — cada posição na organização pode usar o provider/modelo mais adequado ao seu papel (e custo), em paralelo.

### 1.2 Tese central

> O actor model é o substrato natural para organizações de agentes: um ator tem identidade, estado privado, caixa de correio, comunica exclusivamente por mensagens assíncronas, e vive numa hierarquia de supervisão. Estas propriedades mapeiam diretamente para um colaborador numa empresa — identidade, conhecimento próprio, inbox, comunicação formal, e um chefe que responde pelas suas falhas.

### 1.3 Estratégia: interno primeiro, produto depois *(decisão — resolve Q1)*

A solução nasce como **deployment interno para um caso de uso concreto**, com a intenção explícita de **evoluir para produto/framework** utilizável por terceiros. Esta decisão tem consequências de desenho imediatas — coisas que não construímos na v1, mas que não podemos tornar impossíveis:

1. **Fronteira de tenant desde o dia zero.** Toda a entidade do domínio (posição, mensagem, evento, custo) carrega um `OrganizationId`, mesmo que na v1 exista apenas uma organização por deployment. Migrar para multi-tenancy torna-se uma mudança de hosting, não de modelo de dados.
2. **Nada hard-coded ao caso interno.** O caso de uso interno é o *primeiro cliente* da plataforma, não a plataforma: estruturas, prompts, conectores e políticas vivem em configuração/plugins, nunca em código do núcleo.
3. **Conectores como plugins com contrato público estável** (`IConnector`) — o ecossistema de extensão é o principal ativo de produto futuro.
4. **API pública desde a F1.** A Web UI consome a mesma API REST/SignalR que um terceiro consumiria; não existem "atalhos internos".
5. **Documentação como produto.** Esta bíblia evolui para a documentação de arquitetura pública; decisões registam-se em ADRs desde já.

O que fica explicitamente adiado: billing/licenciamento, isolamento forte entre tenants (infra dedicada vs. partilhada), marketplace. São P2/F3, mas o modelo de dados já não os bloqueia.

### 1.4 Primeiro cliente interno: Engenharia/Delivery *(decisão — resolve Q10)*

O primeiro caso de uso interno é uma organização de **Engenharia/Delivery**. A plataforma será validada com agentes e pessoas que executam trabalho típico de delivery de software:

1. **Triagem de bugs e pedidos** — receber entradas externas/internas, classificar severidade, pedir contexto em falta e encaminhar para a posição correta.
2. **Conversão de pedidos em tarefas** — transformar requisitos, bugs ou pedidos operacionais em diretivas/tarefas estruturadas com contexto, prioridade, prazo e critérios de conclusão.
3. **Acompanhamento de progresso** — recolher relatórios, detetar bloqueios, lembrar prazos e agregar estado por equipa/unidade.
4. **Reporting de bloqueios e progresso** — produzir relatórios ascendentes para líderes de delivery e escalar decisões fora da alçada do agente.

Este caso de uso guia a F0/F1: os conectores prioritários são issue trackers/APIs HTTP, email ou canal equivalente para entrada humana, e Web UI para organograma, inbox, aprovações e estado de trabalho.

---

## 2. Conceitos Fundamentais (Glossário)

| Termo | Definição |
|-------|-----------|
| **Organização** | A instância raiz do sistema — equivalente a uma empresa. Contém a hierarquia completa de unidades e posições. |
| **Unidade** | Agrupamento estrutural (departamento, equipa, célula). Forma a árvore organizacional. |
| **Posição** | Um lugar na estrutura (ex.: "Analista de Marketing Sénior"). Tem responsabilidades, permissões e relações definidas. Uma posição é ocupada por um **Ocupante**. |
| **Ocupante** | Quem ocupa uma posição: um **Agente AI** ou uma **Pessoa Real**. A posição é estável; o ocupante pode mudar (substituir um agente por uma pessoa, ou vice-versa, sem alterar a estrutura). |
| **Agente AI** | Ocupante artificial: um ator com configuração de AI (provider, modelo, parâmetros) e uma prompt de identidade. |
| **Pessoa Real** | Ocupante humano: um ator-proxy que encaminha mensagens de/para a pessoa através de conectores (email, Teams, app, etc.). |
| **Prompt de Identidade** | A "ficha de função" do agente: quem é, o que faz, como comunica, o que pode e não pode decidir, a quem reporta. |
| **Canal Vertical** | Comunicação ao longo da cadeia de comando: diretivas (descem), relatórios/escalamentos (sobem). |
| **Canal Horizontal** | Comunicação entre pares da mesma unidade (ou entre unidades, se autorizada). |
| **Conector** | Adaptador entre o sistema e o mundo exterior: email, chat corporativo, APIs, ficheiros, web, calendário, etc. |
| **Provider de AI** | Serviço de inferência (Anthropic, OpenAI, Azure OpenAI, modelos locais via Ollama/vLLM, etc.). Vários podem coexistir em paralelo. |
| **Diretiva** | Mensagem vertical descendente: tarefa, ordem ou política emitida por um superior. |
| **Relatório** | Mensagem vertical ascendente: resultado, progresso ou escalamento. |
| **Memorando** | Mensagem horizontal entre pares. |

---

## 3. Princípios Arquiteturais

1. **Tudo é um ator.** Posições, unidades, conectores, o registo de providers — tudo vive como atores com supervisão.
2. **Posição ≠ Ocupante.** A estrutura organizacional é independente de quem (ou o quê) ocupa cada lugar. Trocar um agente por uma pessoa é trocar o ocupante, não a posição.
3. **Comunicação exclusivamente por mensagens.** Nenhum ator acede ao estado de outro. Isto vale também para humanos: uma pessoa "fala" com o sistema através do seu ator-proxy.
4. **Protocolo de mensagens organizacional.** As mensagens não são texto livre entre atores — são tipos estruturados (Diretiva, Relatório, Memorando, Pedido, Resposta) com metadados (remetente, destinatário, prioridade, prazo, correlação, thread).
5. **Neutralidade de provider.** A camada de AI é uma abstração; nenhum ator conhece detalhes do provider. Configuração por posição, hot-swap possível.
6. **Supervisão e resiliência.** Falhas de um agente (timeout do provider, resposta inválida, exceção) são tratadas pela hierarquia de supervisão Akka.NET — restart, escalamento, ou notificação ao superior organizacional.
7. **Auditabilidade total.** Toda a comunicação é registada (event sourcing). A organização tem "memória institucional" reconstruível.
8. **Humanos têm latência.** O sistema assume que respostas humanas demoram horas/dias; o desenho de timeouts, lembretes e delegação tem isso em conta.
9. **Custos são uma dimensão de primeira classe.** Cada chamada a um provider tem custo; o sistema mede, limita (budgets por posição/unidade) e reporta.

---

## 4. Modelo Organizacional

### 4.1 Estrutura

A organização é uma árvore:

```
Organização (raiz)
├── Direção Geral (Unidade)
│   └── CEO (Posição) ← Agente ou Pessoa
├── Departamento de Marketing (Unidade)
│   ├── Diretor de Marketing (Posição)
│   ├── Equipa de Conteúdos (Unidade)
│   │   ├── Líder de Conteúdos (Posição)
│   │   ├── Redator A (Posição)
│   │   └── Redator B (Posição)
│   └── Equipa de Análise (Unidade)
│       └── ...
└── Departamento de Engenharia (Unidade)
    └── ...
```

Cada **Unidade** tem exatamente uma posição de liderança (o "comandante" da unidade). A árvore organizacional é **lógica**, mantida no `registry` e nas referências das mensagens. Não se espelha fisicamente na árvore de atores Akka.NET: os `PositionActor` são entidades sharded e podem viver em qualquer nó do cluster. A hierarquia de comando é uma relação de domínio; a supervisão técnica Akka trata falhas de processo. *(Decisão Q3 — ver §5.7.)*

### 4.2 Comunicação Vertical (Comando)

- **Descendente — Diretivas.** Um superior emite uma `Diretiva` para um subordinado direto. Diretivas podem ser decompostas: o líder de unidade recebe uma diretiva e parte-a em sub-diretivas para a sua equipa (delegação em cascata).
- **Ascendente — Relatórios e Escalamentos.** Subordinados enviam `Relatórios` (progresso, conclusão) e `Escalamentos` (bloqueios, decisões fora da sua alçada). Regra: um ator só comunica verticalmente com o seu superior direto — não salta níveis, exceto por mecanismos de exceção definidos por política de auditoria/compliance (ex.: canal de denúncia).

### 4.3 Comunicação Horizontal (Colaboração)

- Pares da mesma unidade trocam `Memorandos` e `Pedidos` livremente.
- Comunicação horizontal **entre unidades diferentes** é permitida apenas por **contratos explícitos entre unidades** (`allowed_peer_channels`, tipos de pedido, limites e regras de escalamento). Líderes mediam quando não existe contrato, há recusa, ou surge conflito. *(Decisão Q4.)*

### 4.4 Padrões de trabalho

| Padrão | Descrição |
|--------|-----------|
| **Delegação em cascata** | Diretiva → decomposição → sub-diretivas → agregação de relatórios → relatório ao nível acima. |
| **Pedido entre pares** | Agente A pede ajuda/informação a B; B responde ou recusa com justificação. |
| **Contrato inter-unidades** | Uma unidade expõe canais autorizados para outra unidade, com tipos de pedido, limites e regras de escalamento. |
| **Escalamento** | Agente sem competência/autorização para decidir empurra a decisão para cima, com contexto. |
| **Aprovação humana** | Determinadas ações (configuráveis por posição) exigem aprovação de uma pessoa real antes de execução. |
| **Reunião de equipa** | (P2) Broadcast estruturado dentro de uma unidade com agregação de respostas. |

### 4.5 Ritmo organizacional: agentes híbridos *(decisão — resolve Q6)*

Os agentes são **reativos por omissão e proativos por agenda**. Tal como um colaborador real, um agente não age apenas quando recebe uma ordem — também tem rotinas:

| Tipo de atividade | Disparo | Exemplos |
|---|---|---|
| **Reativa** | Mensagem organizacional recebida | Executar diretiva, responder a pedido de par, processar email externo |
| **Proativa agendada** | Agenda da posição (cron-like) | Relatório diário ao superior, verificação matinal da inbox externa, monitorização semanal de KPIs |
| **Proativa por evento** | Subscrição de eventos do sistema | Prazo de uma diretiva a aproximar-se, par bloqueado há >24h, budget a 80% |

Princípios que disciplinam a proatividade (proatividade descontrolada = custos descontrolados):

1. **Toda a proatividade é declarada.** A agenda faz parte da configuração da posição (`schedule:`), visível e auditável — nenhum agente "decide sozinho" ter novas rotinas.
2. **Pulsos, não loops.** O agente não corre em contínuo; o scheduler entrega-lhe um `Pulse` (mensagem como outra qualquer) no momento agendado. Entre pulsos, o ator está passivo (e pode ser hibernado pelo cluster).
3. **Budget separado.** A atividade proativa consome um budget próprio, distinto do reativo, para que rotinas nunca canibalizem a capacidade de resposta a ordens.
4. **Horário de funcionamento.** Cada posição tem `working_hours`; fora delas, só atividade marcada como crítica.

### 4.6 Definição da organização: GitOps + registry *(decisão — resolve Q7)*

Na F0/F1, a definição da organização usa um modelo híbrido:

1. **YAML/GitOps como source of truth.** Organizações, unidades, posições, ocupantes, prompts, schedules, subscriptions, autoridade e contratos inter-unidades vivem em ficheiros versionados.
2. **Importação para `registry` e read models.** No arranque/deploy, a configuração é validada e materializada em PostgreSQL para consulta rápida pela API, atores e UI.
3. **UI inicialmente read-only.** A consola mostra organograma, estado, inbox e aprovações, mas não edita a estrutura na F0.
4. **Edição visual posterior.** Em F2/F3, a UI pode editar a organização, mantendo export/import para YAML para não perder auditabilidade e revisão por Git.

---

## 5. Arquitetura Técnica de Alto Nível

### 5.1 Stack base (proposta inicial)

| Camada | Tecnologia | Notas |
|--------|-----------|-------|
| Runtime | .NET 8/9 | LTS |
| Actor model | **Akka.NET** (ADR-001 — aceite) | **Akka.Cluster + Cluster Sharding desde a F0** (decisão Q2), Akka.Persistence para event sourcing |
| Persistência | PostgreSQL (event journal + snapshots + read models + audit/registry/budgets) | Via Akka.Persistence.PostgreSql na F0; vector store separado para memória semântica em F1/F2 — ADR-003 |
| Camada de AI | `Microsoft.Extensions.AI` + camada HIVE | Contrato comum para chat/streaming/tool calls; HIVE trata políticas, custos, auditoria, fallback e routing — ADR-002 |
| API / UI | ASP.NET Core (REST + SignalR para tempo real) | Consola de administração e "organograma vivo" |
| Conectores | Plugins (assemblies) com contrato comum | Email (SMTP/IMAP/Graph), Teams/Slack, HTTP, ficheiros, calendário |
| Observabilidade | OpenTelemetry + dashboards | Métricas de custos, latência, filas |

### 5.2 Topologia de atores (visão simplificada)

```
/user
├── organization                      (ator raiz da organização)
│   ├── unit-direcao
│   │   └── pos-ceo                   (PositionActor)
│   │       └── occupant              (AiAgentActor | HumanProxyActor)
│   ├── unit-marketing
│   │   ├── pos-diretor-mkt
│   │   ├── unit-conteudos
│   │   │   ├── pos-lider
│   │   │   ├── pos-redator-a
│   │   │   └── pos-redator-b
│   │   └── ...
│   └── ...
├── ai-gateway                        (router para providers)
│   ├── provider-anthropic            (pool de workers, rate limiting)
│   ├── provider-openai
│   └── provider-ollama
├── connector-hub
│   ├── connector-email
│   ├── connector-teams
│   └── connector-http
└── system-services
    ├── audit-log                     (event sourcing de toda a comunicação)
    ├── cost-tracker
    ├── scheduler                     (timeouts, lembretes, tarefas cron)
    └── registry                      (diretório organizacional pesquisável)
```

### 5.3 O PositionActor

O `PositionActor` é o coração do modelo:

- Mantém o **estado da posição**: inbox organizacional, tarefas em curso, histórico recente.
- Encaminha o trabalho para o seu **ocupante** (ator-filho): `AiAgentActor` ou `HumanProxyActor`.
- Aplica **políticas da posição**: o que pode decidir sozinho, o que exige aprovação, budget de AI, horário de funcionamento.
- Sobrevive à troca de ocupante (a inbox e o histórico pertencem à posição, não ao ocupante).

### 5.4 O AiAgentActor

Ciclo de vida de processamento de uma mensagem:

1. Recebe mensagem organizacional (Diretiva/Memorando/Pedido/...).
2. Monta o contexto: prompt de identidade + memória relevante + a mensagem + ferramentas disponíveis (conectores autorizados).
3. Envia pedido ao `ai-gateway` (que resolve provider/modelo/parâmetros da sua configuração).
4. Interpreta a resposta: pode ser (a) resposta direta, (b) uso de ferramenta/conector, (c) envio de novas mensagens organizacionais (delegar, perguntar a um par, escalar), (d) pedido de aprovação humana.
5. Atualiza memória e emite eventos de auditoria.

O agente funciona em **loop agêntico** — pode iterar (ferramenta → resultado → nova inferência) até concluir, dentro de limites configurados (máx. iterações, budget, timeout).

### 5.5 O HumanProxyActor

- Representa uma pessoa real na estrutura.
- Recebe mensagens organizacionais e entrega-as via conector preferido da pessoa (email, Teams, app web/mobile).
- Recebe respostas humanas pelos mesmos canais e converte-as em mensagens organizacionais.
- Gere latência humana: lembretes, prazos, ausências (out-of-office → delegação automática a substituto — P1).

### 5.6 Scheduler e modelo de execução híbrido

O subsistema `scheduler` (em `system-services`) materializa a decisão Q6:

- **Agendas persistentes** — expressões cron-like por posição, guardadas com o estado da posição (sobrevivem a restarts e a movimentação de shards). Implementação: Akka.Quartz.Actor ou scheduler próprio sobre Akka.Persistence — *decisão ADR-004*.
- **Entrega como mensagem** — no disparo, o scheduler envia um `Pulse(scheduleId, payload)` ao `PositionActor` via sharding; do ponto de vista do agente, é apenas mais uma mensagem na inbox, com a mesma prioridade/auditoria que as restantes.
- **Subscrições de eventos** — o `audit-log`/event bus publica eventos de domínio (prazo próximo, escalamento pendente, budget); posições subscrevem por configuração e recebem `EventTrigger`.
- **Idempotência** — pulsos carregam identificador determinístico (posição + schedule + janela) para que reentregas em failover de cluster não dupliquem trabalho.

### 5.7 Clustering desde o início *(decisão — resolve Q2)*

A v1 nasce distribuída. Implicações concretas:

1. **Cluster Sharding para PositionActors.** As posições não vivem numa árvore local fixa; são **entidades sharded** (`entityId = OrganizationId/PositionId`), distribuídas e movíveis entre nós, com *remember entities* para posições com agenda ativa e passivação automática das inativas. A árvore organizacional passa a ser **lógica** (mantida no `registry` e nas referências das mensagens), não física — o que resolve também a Q3: **supervisão técnica (Akka) e comando organizacional são hierarquias separadas**; a primeira trata de falhas de processo, a segunda de fluxo de trabalho.
2. **Roles de nó.** Nós com papéis distintos: `agents` (sharding de posições), `gateway` (AI gateway e filas por provider), `connectors` (singletons de conectores stateful, ex.: ligação IMAP), `api` (ASP.NET Core + SignalR). Permite escalar cada preocupação independentemente.
3. **Cluster Singletons** onde a unicidade importa: cada conector inbound, o cost-tracker agregador, o scheduler coordinator.
4. **Split Brain Resolver** configurado desde a F0 (estratégia *keep majority*); sem ele, partições de rede corrompem o estado organizacional.
5. **Persistência obrigatória.** Sem estado apenas-em-memória: todo o estado de posição é Akka.Persistence (event sourcing) + snapshots, porque qualquer ator pode ser movido de nó a qualquer momento.
6. **Hosting F0:** Docker Compose local com 1–3 nós Akka e PostgreSQL, mantendo configuração/discovery abstraídos para suportar Kubernetes com Akka.Management/Cluster.Bootstrap quando houver infraestrutura. *(Decisão Q9.)*

Custo desta decisão (assumido conscientemente): a F0 fica mais pesada — serialização de mensagens obrigatoriamente versionável (nada de serialização .NET por omissão; usar Protobuf/System.Text.Json com schemas), testes multi-nó, e disciplina de imutabilidade nas mensagens desde a primeira linha de código. O benefício: nunca haverá uma "migração para cluster", que é historicamente o tipo de refactoring que mata projetos Akka.

### 5.8 Hosting híbrido *(decisão — resolve Q9)*

A F0 é validada em **Docker Compose**, com um cluster Akka real, PostgreSQL local e roles de nó configuráveis (`agents`, `gateway`, `connectors`, `api`). Isto permite desenvolver e testar sharding, persistence, SBR e mensagens versionadas sem depender de uma plataforma Kubernetes disponível.

O desenho mantém **Kubernetes-ready** desde o início:

- discovery e seed nodes são abstraídos por configuração;
- imagens/serviços são separados por role, mesmo que em dev corram no mesmo processo ou composição;
- Akka.Management/Cluster.Bootstrap é o caminho de produção previsto;
- manifests/Helm ficam fora da F0 estrita, mas a arquitetura não assume endereços fixos nem single-node.

---

## 6. Camada de AI Multi-Provider

### 6.1 Abstração

A camada de AI usa `Microsoft.Extensions.AI` como contrato base, envolvido por uma camada HIVE que aplica políticas organizacionais antes/depois da chamada ao provider. Deve suportar:

- Chat completion com system prompt, histórico e tool calling.
- Streaming (para UI) e não-streaming.
- **Batch** (APIs de batch dos providers para trabalho diferido de baixo custo).
- Embeddings (para memória semântica — P1).

A camada HIVE é responsável por provider routing, fallback, orçamento, auditoria, autorização de ferramentas e normalização de metadados de custo/tokens. Semantic Kernel não é adotado como base de orchestration na F0; pode ser integrado futuramente como adapter/ferramenta se trouxer valor claro.

### 6.2 Configuração por posição/agente

```yaml
position: redator-a
occupant:
  type: ai-agent
  ai:
    provider: anthropic           # registado no ai-gateway
    model: claude-sonnet-4-6
    temperature: 0.7
    max_tokens: 4096
    processing: interactive       # interactive | batch
    batch_window: null            # ex.: "daily-02:00" se batch
    fallback:                     # cadeia de fallback se o provider falhar
      - provider: openai
        model: gpt-4.1
    budget:
      max_eur_per_day: 5.00
      max_calls_per_hour: 60
      proactive_max_eur_per_day: 1.00   # budget separado para rotinas (Q6)
  schedule:                             # proatividade declarada (§4.5)
    - id: relatorio-diario
      cron: "0 0 18 * * MON-FRI"
      timezone: Europe/Lisbon
      instruction: "Compilar e enviar relatório diário de progresso ao superior"
  subscriptions:
    - event: directive-deadline-approaching
      within: PT4H
  working_hours: "09:00-18:00 Europe/Lisbon"
  identity_prompt_ref: prompts/redator-a-v3.md
  tools:                          # conectores/ferramentas autorizados
    - connector: http
      scope: ["https://api.empresa.pt/*"]
    - connector: files
      scope: ["read:/shared/marketing"]
  authority:
    can_decide: ["conteudo-blog", "respostas-redes-sociais"]
    must_escalate: ["compromissos-orcamentais", "comunicacao-externa-oficial"]
    requires_human_approval: ["publicacao-final"]
```

### 6.3 AI Gateway

Ator (ou subsistema de atores) responsável por:

- Routing do pedido para o provider correto.
- **Rate limiting e filas por provider** (cada provider tem limites próprios).
- Retries com backoff, circuit breaker, e fallback para a cadeia configurada.
- Agregação de pedidos batch e submissão às APIs de batch.
- Medição de tokens/custos por chamada → `cost-tracker`.

---

## 7. Identidade e Memória dos Agentes

### 7.1 Prompt de Identidade

Documento versionado (Git ou base de dados) com estrutura recomendada:

1. **Identidade** — nome, posição, unidade, a quem reporta, quem lidera.
2. **Missão e responsabilidades** — o que faz e porquê.
3. **Competências e estilo** — tom, idioma (ex.: português europeu), formatos preferidos.
4. **Alçada de decisão** — espelho legível da secção `authority` da configuração.
5. **Protocolo de comunicação** — quando usar Diretiva vs. Memorando vs. Escalamento; etiqueta organizacional.
6. **Limites e segurança** — o que nunca pode fazer.

O sistema injeta automaticamente contexto dinâmico (organograma relevante, lista de pares, ferramentas disponíveis) — o autor da prompt não precisa de o manter manualmente.

### 7.2 Memória (proposta em camadas)

| Camada | Conteúdo | Implementação |
|--------|----------|---------------|
| Curto prazo | Conversa/tarefa atual | Estado do ator |
| Médio prazo | Histórico recente da posição, decisões | Akka.Persistence (event sourcing) + sumarização periódica |
| Longo prazo | Conhecimento institucional, documentos | Vector store (P1) + pesquisa no audit-log |

---

## 8. Conectores com o Mundo Real

Contrato comum (`IConnector`): identificação, capacidades (inbound/outbound), esquema de configuração, mapeamento mensagem externa ↔ mensagem organizacional.

| Conector | Direção | Casos de uso | Prioridade |
|----------|---------|--------------|-----------|
| Email (SMTP/IMAP ou MS Graph) | In/Out | Canal principal para pessoas reais; receção de pedidos externos | P0 |
| Teams / Slack | In/Out | Canal interativo para pessoas | P1 |
| HTTP/REST genérico | Out | Agentes atuam sobre APIs externas | P0 |
| Webhooks | In | Sistemas externos despoletam trabalho | P1 |
| Ficheiros / SharePoint / Drive | In/Out | Documentos | P1 |
| Calendário | In/Out | Agendamento, prazos | P2 |
| Web UI própria | In/Out | Consola: organograma vivo, inbox humana, aprovações | P0 |

Segurança dos conectores: todo o conteúdo externo é **dados, não instruções** — mitigação de prompt injection (ver §10).

---

## 9. Protocolo de Mensagens (esboço)

```csharp
abstract record OrgMessage(
    MessageId Id,
    PositionRef From,
    PositionRef To,
    ThreadId Thread,          // correlação de conversas
    Priority Priority,
    DateTimeOffset SentAt,
    DateTimeOffset? Deadline);

record Directive(... , string Objective, string Context, DirectiveId? Parent) : OrgMessage;
record Report(... , DirectiveId About, ReportKind Kind /* Progress|Done|Blocked */, string Body) : OrgMessage;
record Escalation(... , string Issue, string Context, string[] OptionsConsidered) : OrgMessage;
record Memo(... , string Body) : OrgMessage;          // horizontal
record PeerRequest(... , string Ask) : OrgMessage;    // horizontal, espera resposta
record PeerResponse(... , MessageId InReplyTo, string Body) : OrgMessage;
record ApprovalRequest(... , string Action, string Justification) : OrgMessage;
record ApprovalDecision(... , bool Approved, string? Reason) : OrgMessage;
```

Regras de encaminhamento (verticalidade, permissões horizontais) são validadas pelo `PositionActor` e violações são auditadas.

---

## 10. Segurança e Governança

- **Alçadas explícitas** por posição (decidir / escalar / aprovar por humano).
- **Prompt injection**: conteúdo vindo de conectores é marcado como não-fidedigno; instruções embebidas em emails/documentos não são executadas sem confirmação.
- **Budgets** de custo por posição/unidade/organização, com corte automático e alerta.
- **Auditoria imutável** de todas as mensagens e chamadas a AI (event sourcing).
- **Segredos** (API keys, credenciais de conectores) em vault, nunca em prompts.
- **Kill switch** por agente, unidade ou organização.
- **RGPD baseline europeu**: classificação de dados, minimização de contexto enviado para LLMs, políticas por conector, retenção configurável, direito ao apagamento por redaction/tombstone, e revisão de DPA/providers antes de produção.

---

## 11. Observabilidade

- Organograma vivo: estado de cada posição (idle, a trabalhar, bloqueado, à espera de humano).
- Métricas: mensagens/min, latência por provider, custo por posição/dia, taxa de escalamentos, tarefas concluídas vs. falhadas.
- Tracing distribuído de uma diretiva ao longo da cascata de delegação (correlação por `ThreadId`/`DirectiveId`).

---

## 12. Roadmap Incremental (proposta)

| Fase | Objetivo | Conteúdo |
|------|----------|----------|
| **F0 — Núcleo** | Prova de conceito **já em cluster** para Engenharia/Delivery | Docker Compose com 1–3 nós Akka + PostgreSQL, Akka.Cluster + Sharding de posições, serialização versionada, SBR, persistência PostgreSQL, organização em YAML/GitOps, PositionActor + AiAgentActor, 1 provider via `Microsoft.Extensions.AI`, comunicação vertical, scheduler com `Pulse`, audit log |
| **F1 — Organização mínima viável** | Equipa de delivery real a funcionar | Multi-provider + gateway (rate limit, fallback), contratos de comunicação horizontal, HumanProxyActor + conector email/issue tracker, Web UI read-only (organograma + inbox + aprovações) sobre API pública, subscrições de eventos, roles de nó |
| **F2 — Operação** | Robustez e escala | Multi-nó em produção (K8s), batch processing, budgets/custos, memória de longo prazo (vector store), conectores Teams/Slack/ficheiros, edição controlada da organização com export/import YAML |
| **F3 — Maturidade** | Plataforma | Editor visual da organização, marketplace de conectores, métricas avançadas, reuniões de equipa, substituições/férias |

---

## 13. Não-Objetivos (v1)

1. **Agentes autónomos sem supervisão** — toda a ação relevante é auditada e sujeita a alçadas; não é objetivo criar uma organização que opere sem qualquer controlo humano.
2. **Treino/fine-tuning de modelos** — usamos modelos via API; não treinamos modelos.
3. **Substituir ferramentas de RH/ERP** — a estrutura organizacional do sistema não pretende ser o sistema de RH da empresa.
4. **Multi-tenancy SaaS na v1** — assumimos uma organização por deployment; o modelo inclui `OrganizationId` desde o início, mas billing, marketplace e isolamento SaaS forte ficam para F3.
5. **Comunicação por voz/telefonia na v1.**

---

## 14. Questões de Arquitetura

| # | Questão | Impacto | Estado |
|---|---------|---------|--------|
| Q1 | Propósito: produto vs. interno | Multi-tenancy, extensibilidade | **Resolvida (v0.2)**: interno → produto. Ver §1.3 |
| Q2 | Escala alvo | Complexidade F0/F1 | **Resolvida (v0.2)**: cluster desde o início. Ver §5.7 |
| Q3 | Supervisão Akka = comando organizacional? | Topologia de atores | **Resolvida (v0.2)**: separadas — sharding torna a árvore organizacional lógica. Ver §5.7 |
| Q4 | Política de comunicação horizontal **entre unidades**: livre, mediada, ou por contrato? | Protocolo de mensagens | **Resolvida (v0.3)**: contratos explícitos entre unidades. Ver §4.3 |
| Q5 | Abstração de AI: própria, `Microsoft.Extensions.AI`, ou Semantic Kernel? | ADR-002 | **Resolvida (v0.3)**: `Microsoft.Extensions.AI` + camada HIVE. Ver ADR-002 |
| Q6 | Agentes proativos vs. reativos | Modelo de execução e custos | **Resolvida (v0.2)**: híbrido com scheduler. Ver §4.5 e §5.6 |
| Q7 | Formato de definição da organização: YAML/ficheiros (GitOps) vs. base de dados + UI? Ambos? | UX de administração | **Resolvida (v0.3)**: YAML/GitOps como source of truth em F0/F1, importado para registry/read models. Ver §4.6 |
| Q8 | Requisitos de RGPD/compliance concretos (sector do utilizador final)? | §10 | **Resolvida (v0.3)**: baseline europeu. Ver §10 |
| Q9 | Ambiente de hosting: Kubernetes disponível? On-prem ou cloud (qual)? | §5.7, discovery do cluster | **Resolvida (v0.3)**: Docker Compose em F0, Kubernetes-ready por abstração de configuração/discovery. Ver §5.8 |
| Q10 | Qual é o **caso de uso interno concreto** (primeiro cliente)? Que organização, que funções, que conectores prioritários? | Valida todo o desenho; define a F1 | **Resolvida (v0.3)**: Engenharia/Delivery. Ver §1.4 |

---

## 15. Registo de Decisões (ADRs)

| ADR | Título | Estado |
|-----|--------|--------|
| ADR-001 | Adoção de Akka.NET como substrato de actor model | **Aceite (v0.2)** — detalhe abaixo |
| ADR-002 | Camada de abstração de AI (própria vs. Microsoft.Extensions.AI vs. Semantic Kernel) | **Aceite (v0.3)** — detalhe abaixo |
| ADR-003 | Estratégia de persistência (PostgreSQL event sourcing) | **Aceite (v0.3)** — detalhe abaixo |
| ADR-004 | Implementação do scheduler (Akka.Quartz.Actor vs. scheduler próprio persistente) | Aberto |

### ADR-001: Akka.NET como substrato de actor model

**Estado:** Aceite · **Data:** 2026-06-12 · **Contexto:** precisamos de um runtime de atores em .NET com clustering desde o início (Q2), supervisão, persistência/event sourcing e maturidade de produção.

#### Opções consideradas

| Dimensão | **Akka.NET** | Microsoft Orleans | Proto.Actor | Dapr Actors |
|---|---|---|---|---|
| Modelo | Atores clássicos (explícitos, hierarquia, supervisão) | Virtual actors (grains) — ativação implícita | Atores clássicos, minimalista | Virtual actors sobre sidecar |
| Clustering | Akka.Cluster + Sharding, maduro, controlo fino | Excelente e mais simples (gestão automática de grains) | Cluster existente mas ecossistema menor | Delegado à infra Dapr |
| Supervisão hierárquica | **Nativa e central** | Limitada (não há árvore de supervisão) | Nativa | Inexistente |
| Event sourcing | Akka.Persistence integrado | Possível mas não nativo (JournaledGrain ou externo) | Via extensões | Externo |
| Comunicação por mensagens tipadas com mailbox/stash/prioridades | Completa | Abstraída em chamadas de método (RPC-like) | Completa | RPC-like |
| Curva de aprendizagem | Alta | Média | Média | Baixa |
| Comunidade/manutenção .NET | Petabridge, ativa | Microsoft, ativa | Pequena | Microsoft/CNCF |

#### Decisão e fundamentação

Adotamos **Akka.NET**. O fator decisivo é conceptual: a nossa tese (§1.2) assenta em atores **explícitos**, com identidade, mailbox, mensagens tipadas e supervisão hierárquica — exatamente o modelo clássico do Akka. O Orleans seria operacionalmente mais simples em cluster, mas o seu modelo de *virtual actors* esconde a mailbox e transforma comunicação em RPC, o que enfraquece o protocolo de mensagens organizacional (§9) e elimina a noção de supervisão que queremos espelhar. Proto.Actor é elegante mas o ecossistema é demasiado pequeno para uma aposta de produto; Dapr não oferece o modelo de que precisamos.

#### Consequências

- (+) Mapeamento direto conceito-organizacional → primitivas do framework; Persistence e Sharding resolvem §5.7 e §7.2 sem peças externas.
- (−) Curva de aprendizagem alta e disciplina exigente (imutabilidade, serialização versionada, testes multi-nó) — mitigada por adotar estas práticas desde a F0.
- (⚠) Risco de dependência da Petabridge para o ritmo de evolução do framework — aceitável; projeto Apache 2.0 com forkability real.
- Revisitar se: a equipa não dominar Akka em tempo útil na F0, ou se surgirem limites de sharding com dezenas de milhares de posições por organização (improvável na v1).

---

### ADR-002: `Microsoft.Extensions.AI` como base da camada de AI

**Estado:** Aceite · **Data:** 2026-06-15 · **Contexto:** precisamos de uma abstração multi-provider que suporte chat, streaming, tool calling, batch futuro, medição de custos e troca de providers sem acoplar o domínio HIVE a SDKs concretos.

#### Opções consideradas

| Opção | Vantagens | Limitações |
|---|---|---|
| Interface própria HIVE (`IChatCompletionProvider`) | Controlo total e contrato mínimo ajustado ao domínio | Risco de reinventar contratos que o ecossistema .NET já está a normalizar |
| `Microsoft.Extensions.AI` + camada HIVE | Contrato .NET comum, adapters existentes/em crescimento, baixo acoplamento a SDKs específicos | Pode exigir wrappers para metadados avançados de custo, tool policies e batch |
| Semantic Kernel | Orchestration e ferramentas já opinadas | Enfraquece a separação entre runtime organizacional HIVE e framework de orchestration; maior acoplamento |

#### Decisão e fundamentação

Adotamos **`Microsoft.Extensions.AI` como contrato base**, envolvido por uma camada HIVE. A camada HIVE mantém o domínio: resolve provider/modelo por posição, aplica budgets, autoriza ferramentas, audita chamadas, normaliza custos/tokens, implementa fallback e integra com o `ai-gateway`.

Semantic Kernel não é adotado como base da F0. Pode ser usado futuramente como adapter ou ferramenta especializada, mas não como núcleo de orchestration, porque a orchestration principal pertence ao protocolo organizacional e aos atores.

#### Consequências

- (+) Menos código próprio para contratos básicos de chat/streaming/tool calling.
- (+) Provider neutrality fica alinhada com o ecossistema .NET.
- (+) A camada HIVE continua a ser o ponto único para políticas organizacionais, custos, auditoria e segurança.
- (−) Será preciso validar cedo se os providers necessários expõem metadados suficientes através dos adapters existentes.
- Revisitar se: `Microsoft.Extensions.AI` não cobrir tool calling/batch/metadata de forma suficiente para os providers prioritários da F1.

---

### ADR-003: PostgreSQL como persistência base da F0

**Estado:** Aceite · **Data:** 2026-06-15 · **Contexto:** a F0 precisa de persistência obrigatória para Akka.Cluster Sharding, event sourcing, snapshots, auditabilidade e read models, sem introduzir demasiadas peças operacionais antes do primeiro vertical slice.

#### Opções consideradas

| Opção | Vantagens | Limitações |
|---|---|---|
| PostgreSQL para tudo na F0 | Operação simples, tecnologia madura, suporta journal/snapshots/read models/audit/registry/budgets | Não é ideal como vector store especializado a longo prazo |
| PostgreSQL na F0 + vector store separado em F1/F2 | Mantém a F0 simples e deixa a memória semântica evoluir para tecnologia própria | Exige fronteira clara entre memória/audit log e pesquisa semântica |
| Event store dedicado desde F0 | Modelo de event sourcing mais especializado | Mais complexidade operacional antes de validar o produto |

#### Decisão e fundamentação

Adotamos **PostgreSQL como persistência base da F0**, incluindo Akka.Persistence journal/snapshots, read models, audit log, registry e budgets. A memória semântica de longo prazo fica prevista como **vector store separado** em F1/F2, consumindo eventos/documentos já auditados.

Esta decisão privilegia simplicidade operacional e consistência inicial. A separação futura da pesquisa semântica evita forçar embeddings e retrieval avançado para dentro do mesmo modelo de persistência transacional.

#### Consequências

- (+) Menos infraestrutura na F0; Docker Compose consegue validar o sistema inteiro.
- (+) Event sourcing, read models e auditoria começam na mesma base transacional.
- (+) A futura vector store pode ser escolhida com base em necessidades reais de memória e retrieval.
- (−) Será necessário desenhar fronteiras claras entre eventos/auditoria imutável, read models mutáveis e índices semânticos derivados.
- Revisitar se: volume de eventos, requisitos de auditoria ou necessidades de replay excederem o conforto operacional do PostgreSQL.

---

*Próxima iteração (v0.4): ADR-004 (scheduler), aprofundamento da memória dos agentes (§7.2), protocolo de mensagens (§9) com máquinas de estados das diretivas, e definição do primeiro vertical slice de Engenharia/Delivery para F0.*
