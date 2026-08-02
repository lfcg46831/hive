# Post-F0.10 error analysis v1

Date: 2026-08-02
Scope: análise diagnóstica dos datasets já produzidos por `US-F0-21-T01`
Nature: **read-only**. Não executa corpus, não consome run ids, não altera configuração,
não reabre nem inspeciona o holdout como avaliação.

## Entradas

| Input | Identity |
| --- | --- |
| Relatório congelado | `evidence/evaluation/bug-triage-lab-v2/post-f0-10-calibration-report.v1.md` |
| Datasets | `artifacts/evaluation/post-f0-10-calibration-00{1,2,3}.json` |
| Baseline | `config/organizations/acme-delivery/examples/evaluation/bug-triage-corpus.v1.json` |
| Rubrica | `config/organizations/acme-delivery/examples/evaluation/bug-triage-rubric.v1.json` |
| Holdout (só estrutura de labels) | `config/organizations/acme-delivery/examples/evaluation/bug-triage-holdout-corpus.v1.json` |

As métricas oficiais continuam a ser as do relatório congelado, que divide por 30 casos.
Os agregados recalculados neste documento são sobre casos **pontuáveis** (28/27/27) e
estão marcados como tal; servem para diagnóstico, não substituem o relatório.

## 1. Estrutura do baseline: lacuna e escalamento são a mesma variável

No corpus de calibração, `missing-information` não-vazio ocorre **se e só se**
`decision = escalation`:

| Corpus | lacuna → escalation | lacuna → report | sem lacuna → escalation | sem lacuna → report |
| --- | ---: | ---: | ---: | ---: |
| calibração v1 (n=30) | 6 | 0 | 0 | 24 |
| holdout v1 (n=30) | 1 | 12 | 7 | 10 |

Os dois corpora codificam gatilhos de escalamento **disjuntos**. Na calibração o gatilho é
insuficiência de evidência. No holdout é decisão fora da autoridade da posição:
rollback vs. alteração de preços time-sensitive (`holdout-003`), obrigação de preservação
legal (`holdout-007`), exceção de firewall a parceiro (`holdout-011`), interpretação de
regulador (`holdout-015`), waiver de acessibilidade (`holdout-019`), continuidade perante
outage de parceiro (`holdout-023`), conflito support/engineering (`holdout-030`).

Consequências:

- Uma regra `lacuna ⇒ escalation` obtém agreement e recall de 1.0 na calibração sem
  qualquer capacidade real, e produziria no holdout recall 1/8 com 12 falsos positivos.
  Qualquer materialidade afinada contra a calibração é um falso verde.
- Na calibração, as dimensões `missing-information` (peso 0.35) e `decision` não são
  sinais independentes.

## 2. Matrizes de decisão

| Run | TP | FN | FP | TN | Não classificados | Agreement | Recall |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `001` | 4 | 2 | 6 | 17 | 1 (`timeout`, `triage-009`) | 0.700 | 0.667 |
| `002` | 2 | 4 | 1 | 22 | 1 (`ai-output-invalid`, `triage-029`) | 0.800 | 0.333 |
| `003` | 2 | 4 | 5 | 19 | 0 | 0.700 | 0.333 |

Com 6 positivos no corpus, `escalation recall ≥ 0.90` exige TP=6 e FN=0 exatos
(5/6 = 0.833 falha). `decision agreement ≥ 0.90` permite no máximo 3 erros em 30.

## 3. Falsos negativos: inconsistência interna, não cegueira

Em 8 dos 10 FN o modelo **listou as lacunas e mesmo assim** fechou `Report.Done` com
`work_state = Completed`:

| Run | Caso | Lacunas gold | Lacunas previstas | Intenção proposta | Resolvido |
| --- | --- | ---: | ---: | --- | --- |
| `001` | `triage-015` | 4 | 8 | `Report.Done` | `Report.Done` |
| `001` | `triage-030` | 5 | 7 | `Report.Done` | `Report.Done` |
| `002` | `triage-005` | 3 | 3 | `Report.Done` | `Report.Done` |
| `002` | `triage-015` | 4 | 6 | `Report.Done` | `Report.Done` |
| `002` | `triage-025` | 5 | 0 | `Report.Done` | `Report.Done` |
| `002` | `triage-030` | 5 | 7 | `Report.Done` | `Report.Done` |
| `003` | `triage-011` | 4 | 5 | `Report.Done` | `Report.Done` |
| `003` | `triage-015` | 4 | 8 | `Report.Done` | `Report.Done` |
| `003` | `triage-025` | 5 | 0 | `Report.Done` | `Report.Done` |
| `003` | `triage-030` | 5 | 8 | `Report.Done` | `Report.Done` |

Apenas `triage-025` (em `002` e `003`) é falha de deteção. `triage-015` e `triage-030`
falham nos três runs; `triage-025` em dois.

## 4. Falsos positivos: dois mecanismos distintos

| Run | Caso | Lacunas previstas | Intenção proposta | Razão da resolução |
| --- | --- | ---: | --- | --- |
| `001` | `triage-001` | 5 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `001` | `triage-003` | 1 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `001` | `triage-012` | 3 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `001` | `triage-023` | 7 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `001` | `triage-026` | 5 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `002` | `triage-007` | 5 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `003` | `triage-006` | 5 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `003` | `triage-024` | 4 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `003` | `triage-029` | 5 | `Escalation` (`SuperiorDecision`) | `proposal-escalation` |
| `001` | `triage-002` | 0 | `Report.Done` | `verifier-disagreement` |
| `003` | `triage-004` | 0 | `Report.Done` | `verifier-disagreement` |
| `003` | `triage-016` | 0 | `Report.Done` | `verifier-disagreement` |

Nove FP são o modelo a invocar `SuperiorDecision` — o frame de autoridade do holdout —
em casos que o baseline de calibração considera rotina. Três FP são criados pelo runtime,
não pelo modelo (secção 6).

O claim de autoridade é fraco como sinal neste corpus: `required_intervention =
SuperiorDecision` aparece em 7 casos de escalamento gold e em 9 de report gold.

## 5. Qualidade das labels de lacuna: recall alto, precision colapsada

Micro-agregado sobre os três runs:

| Conjunto | Acertos | Previstas | Gold | Precision | Recall |
| --- | ---: | ---: | ---: | ---: | ---: |
| Todos os casos | 61 | 296 | 75 | **0.206** | 0.813 |
| Só casos escalation gold | — | — | — | **0.678** | 0.813 |

Média de labels previstas por caso:

| Run | Casos sem lacuna gold | Casos com lacuna gold |
| --- | --- | --- |
| `001` | 3.2 (n=23), 5 com zero | 6.2 (n=6), 0 com zero |
| `002` | 3.0 (n=23), 5 com zero | 3.5 (n=6), 2 com zero |
| `003` | 2.6 (n=24), 8 com zero | 5.3 (n=6), 1 com zero |

O modelo encontra as lacunas certas e emite cerca de quatro vezes o volume gold. Lista
*o que seria útil saber*; o baseline lista *o que bloqueia a decisão*. A variável em falta
é materialidade, não deteção.

Isto reflete-se diretamente na pontuação da dimensão (casos pontuáveis):

| Run | `missing-information` em casos com lacuna gold | em casos sem lacuna gold |
| --- | ---: | ---: |
| `001` | 0.822 (n=6) | **0.182** (n=22) |
| `002` | 0.881 (n=4) | **0.217** (n=23) |
| `003` | 0.782 (n=5) | **0.273** (n=22) |

### Resultado negativo: a contagem de lacunas não é proxy de materialidade

Sweep sobre os 88 casos pontuáveis dos três runs, regra `nº de lacunas previstas ≥ limiar
⇒ escalation`:

| Limiar | TP | FN | FP | TN | Agreement | Recall | Precision |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 15 | 3 | 52 | 18 | 0.375 | 0.833 | 0.224 |
| 3 | 15 | 3 | 43 | 27 | 0.477 | 0.833 | 0.259 |
| 5 | 12 | 6 | 17 | 53 | 0.739 | 0.667 | 0.414 |
| 6 | 10 | 8 | 7 | 63 | 0.830 | 0.556 | 0.588 |
| 7 | 7 | 11 | 2 | 68 | 0.852 | 0.389 | 0.778 |

Nenhum limiar aproxima simultaneamente FN=0 e ≤3 erros. A correção determinística
ingénua ("se listou lacunas, escala") degrada o agreement de ~0.73 para 0.375.
A materialidade tem de ser um juízo por lacuna, não uma contagem.

## 6. O verifier converte `Undetermined` em escalamento

Quatro overrides nos três runs, todos `Report.Done → Escalation`, todos com
`verifier_classification = Undetermined`:

| Run | Caso | Decisão gold | Efeito |
| --- | --- | --- | --- |
| `001` | `triage-002` | report | FP |
| `002` | `triage-011` | escalation | TP |
| `003` | `triage-004` | report | FP |
| `003` | `triage-016` | report | FP |

O runtime trata "não consegui verificar" como "refutado", contrariando o princípio
`Unknown ≠ Refuted`. Três em quatro estão errados. O efeito líquido de suprimir o override
sob `Undetermined` é pequeno e de sinal misto (`001` 0.700→0.733, `002` 0.800→0.767,
`003` 0.700→0.767); o argumento é de correção de princípio, não de métrica.

## 7. Cenário-tecto: quanto vale resolver a materialidade

Agregados sobre casos pontuáveis, com `severity` e `decision` observados versus um cenário
de materialidade perfeita (lacunas não-materiais suprimidas, decisão consequente):

| Run | `missing-information` atual | no tecto | macro atual | macro no tecto |
| --- | ---: | ---: | ---: | ---: |
| `001` | 0.319 | 0.962 | 0.574 | 0.874 |
| `002` | 0.316 | 0.982 | 0.593 | 0.871 |
| `003` | 0.367 | 0.960 | 0.608 | 0.882 |

Alvos parciais necessários, mantendo `severity` e `decision` constantes em `001`:

- `missing-information ≥ 0.35`: exige lista material vazia em ~22% dos casos limpos.
  Observado ~20%. Praticamente alcançado (`003` já cumpre com 0.367).
- `macro ≥ 0.65`: exige lista material vazia em ~46% dos casos limpos.

## 8. Conclusões

1. Os FN são maioritariamente inconsistência entre a evidência que o modelo reconhece e a
   conclusão que emite — o problema que um facto de suficiência de evidência resolve.
2. A correção determinística por presença ou contagem de lacunas é pior do que o estado
   atual. É necessária classificação de materialidade por lacuna.
3. Os FP dividem-se em claim de autoridade sem fundamentação (9) e override do verifier
   sob `Undetermined` (3). São ramos distintos e precisam de tratamento distinto.
4. A materialidade é o maior lever isolado: leva plausivelmente `missing-information` e
   `macro` acima dos gates, mas **não** garante `decision agreement ≥ 0.90` nem
   `escalation recall ≥ 0.90`, que dependem também do ramo de autoridade.
5. Os gates de decisão são estatisticamente frágeis com 6 positivos: `recall ≥ 0.90`
   equivale a exigir 6/6.
6. Otimizar apenas o ramo de informação passa a calibração e falha o holdout por
   construção (secção 1).
