# AGENTS.md

## Fonte de verdade

- A fonte de verdade deste projeto e `docs/bible.html` (o bible).
- Antes de alterar arquitetura, roadmap, fases, user stories, tarefas, protocolos, ADRs ou decisoes de produto, consulta `docs/bible.html` e mantem as alteracoes alinhadas com ele.
- Se houver conflito entre outro documento, comentario ou suposicao e `docs/bible.html`, segue `docs/bible.html` ou atualiza-o explicitamente com a nova decisao.

## Onde registar informacao

Cada tipo de informacao tem um dono unico; nao dupliques o mesmo conteudo em mais do que um sitio.

- Decisoes de arquitetura e contratos duradouros (seams, protocolos, contratos de configuracao, ADRs, roadmap, fases, user stories) vao para `docs/bible.html`, a fonte de verdade.
- Referencia operacional, ou seja, como configurar e operar (modelo de configuracao, seccoes de `appsettings`, variaveis de ambiente, connection strings, logging) vai para `docs/configuration.md`.
- Narrativa do "o que mudou nesta tarefa" (detalhe passo-a-passo da implementacao de cada US/tarefa) vai para a mensagem de commit, nao para documentacao.

Nao acumules narrativa de implementacao no bible nem nos ficheiros de instrucoes (`AGENTS.md`/`CLAUDE.md`), que devem ficar curtos. Regra rapida: decisao para o bible; como operar para `configuration.md`; o que fiz para o commit.

## Fecho de tarefas

- Sempre que terminares uma tarefa, gera uma mensagem curta em ingles com o resumo do que foi feito para ser usada no commit do git.
