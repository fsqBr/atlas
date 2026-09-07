/**
 * The in-app manual: one section per feature and per configuration surface, in English and Portuguese.
 * Bodies are the Markdown subset the Markdown component renders (headings, lists, tables, code, **bold**,
 * `code`, [links](/route)). `{placeholders}` are replaced with live facts from the API before rendering
 * (see HelpPage.resolvePlaceholders) so the manual describes THIS instance, not a generic one.
 *
 * Keep EN and PT in step: the help test checks that every section has both languages, the same placeholders
 * and the same code fences.
 */
import type { Lang } from "../i18n";

export type HelpGroup = "start" | "assessments" | "portfolio" | "aiEstate" | "settings" | "integrate" | "operate";

export interface HelpSection {
  id: string;
  group: HelpGroup;
  /** Route(s) whose contextual help button opens this section. */
  routes?: string[];
  title: Record<Lang, string>;
  body: Record<Lang, string>;
}

export const HELP_GROUPS: HelpGroup[] = ["start", "assessments", "portfolio", "aiEstate", "settings", "integrate", "operate"];

/** Placeholders resolved from the running instance; documented here so the test can assert they are the only ones used. */
export const HELP_PLACEHOLDERS = [
  "version",
  "origin",
  "auth",
  "roles",
  "tenant",
  "syncEnabled",
  "syncHour",
  "syncNext",
  "priceCatalog",
  "providers",
  "costProviders",
  "unpriced",
] as const;

export const HELP_SECTIONS: HelpSection[] = [
  // ───────────────────────────── Start ─────────────────────────────
  {
    id: "overview",
    group: "start",
    routes: ["/help"],
    title: { en: "What Atlas is", "pt-BR": "O que é o Atlas" },
    body: {
      en: `Atlas is an **enterprise software intelligence** platform that runs entirely inside your infrastructure. It reads source code, project files, manifests, configuration and git history — never executing or building anything — and produces:

- an **inventory** of every system (frameworks, dependencies, databases, front ends, tests);
- **findings** in seven categories (security, secrets, personal data, quality, architecture, modernization, dependencies) with severity, confidence, location, remediation and a stable fingerprint across runs;
- a **health score** (0–100) built from five weighted dimensions, where every lost point is attributed to a rule;
- a **modernization analysis**: six strategies ranked by fit, effort and cost ranges with every assumption printed, what staying legacy costs per year, payback, and a phased roadmap;
- **executive and portfolio reports** (HTML/PDF, English or Portuguese, white-label) plus CSV, JSON, SARIF, SBOM and Markdown exports;
- the **AI Estate**: which AI providers, agent frameworks, MCP servers and models each repository uses, spend from provider billing, usage per developer, budgets, alerts and forecasts.

### How the pieces fit

| Component | Role |
|---|---|
| atlas-web | nginx serving this SPA and proxying \`/api\` |
| atlas-api | REST API, reports, scheduler, OSV sync, migrations |
| atlas-worker | takes jobs from the PostgreSQL queue, materializes sources, runs scanners in a disposable child process |
| PostgreSQL | all data; the governance module keeps its own \`governance\` schema |
| MinIO / volumes | uploads, vulnerability bundle, clone workspaces |
| atlas-pdf | Gotenberg, HTML → PDF, no published port |

**Facts first, AI second.** Deterministic engines produce inventory, findings, score, strategy, cost and roadmap. The optional AI layer only writes prose *about* those facts, is always labelled, and is off until an administrator enables it.

This manual describes the instance you are using: **Atlas v{version}** at \`{origin}\`.`,
      "pt-BR": `O Atlas é uma plataforma de **inteligência de software corporativo** que roda inteiramente na sua infraestrutura. Ele lê código-fonte, arquivos de projeto, manifests, configuração e histórico git — sem executar nem compilar nada — e produz:

- um **inventário** de cada sistema (frameworks, dependências, bancos, front ends, testes);
- **findings** em sete categorias (segurança, segredos, dados pessoais, qualidade, arquitetura, modernização, dependências) com severidade, confiança, localização, remediação e um fingerprint estável entre execuções;
- um **health score** (0–100) composto por cinco dimensões ponderadas, em que cada ponto perdido é atribuído a uma regra;
- uma **análise de modernização**: seis estratégias ordenadas por aderência, faixas de esforço e custo com todas as premissas impressas, quanto custa ficar no legado por ano, payback e um roadmap em fases;
- **relatórios executivo e de portfólio** (HTML/PDF, inglês ou português, white-label) além de exportações CSV, JSON, SARIF, SBOM e Markdown;
- o **AI Estate**: quais provedores de IA, frameworks de agentes, servidores MCP e modelos cada repositório usa, gasto vindo da fatura dos provedores, uso por desenvolvedor, orçamentos, alertas e projeções.

### Como as peças se encaixam

| Componente | Papel |
|---|---|
| atlas-web | nginx servindo esta SPA e fazendo proxy de \`/api\` |
| atlas-api | API REST, relatórios, agendador, sync do OSV, migrations |
| atlas-worker | pega jobs da fila no PostgreSQL, materializa fontes, roda os scanners num processo filho descartável |
| PostgreSQL | todos os dados; o módulo de governança tem o próprio schema \`governance\` |
| MinIO / volumes | uploads, bundle de vulnerabilidades, workspaces de clone |
| atlas-pdf | Gotenberg, HTML → PDF, sem porta publicada |

**Fatos primeiro, IA depois.** Motores determinísticos produzem inventário, findings, score, estratégia, custo e roadmap. A camada de IA opcional só escreve prosa *sobre* esses fatos, é sempre rotulada e fica desligada até um administrador ativá-la.

Este manual descreve a instância que você está usando: **Atlas v{version}** em \`{origin}\`.`,
    },
  },
  {
    id: "instance",
    group: "start",
    title: { en: "Your instance right now", "pt-BR": "Sua instância agora" },
    body: {
      en: `These values are read live from the API each time this page opens.

| Fact | Value |
|---|---|
| Version | {version} |
| Address | {origin} |
| Sign-in | {auth} |
| Your roles | {roles} |
| Tenant | {tenant} |
| Daily billing pull | {syncEnabled}, at {syncHour}:00 UTC, next run {syncNext} |
| Price catalog | {priceCatalog} |
| Models seen without a price | {unpriced} |
| Cost-source providers available | {costProviders} |
| AI signature catalog | {providers} providers |

If sign-in is off, every visitor is an administrator of the default tenant — fine on a laptop, never on a shared instance (see [Sign-in, roles and tenants](/help#auth)).`,
      "pt-BR": `Estes valores são lidos ao vivo da API sempre que esta página abre.

| Fato | Valor |
|---|---|
| Versão | {version} |
| Endereço | {origin} |
| Login | {auth} |
| Seus papéis | {roles} |
| Tenant | {tenant} |
| Puxada diária de fatura | {syncEnabled}, às {syncHour}:00 UTC, próxima execução {syncNext} |
| Catálogo de preços | {priceCatalog} |
| Modelos vistos sem preço | {unpriced} |
| Provedores de fonte de custo disponíveis | {costProviders} |
| Catálogo de assinaturas de IA | {providers} provedores |

Se o login está desligado, todo visitante é administrador do tenant padrão — ok num laptop, nunca numa instância compartilhada (veja [Login, papéis e tenants](/help#auth)).`,
    },
  },
  {
    id: "first-assessment",
    group: "start",
    routes: ["/new"],
    title: { en: "Your first assessment", "pt-BR": "Sua primeira avaliação" },
    body: {
      en: `1. Click **New assessment** and pick a source: a folder under one of the mounted roots (folder picker), a folder on your machine (zipped in the browser and uploaded; \`bin/\`, \`obj/\`, \`node_modules/\` and \`.git/\` are skipped), or a git URL / GitHub / Azure DevOps / GitLab locator. Private repositories need a stored [credential](/help#credentials).
2. The run is queued and the page follows it live. A repository of a few hundred files takes seconds to a couple of minutes.
3. **Overview** shows the score with a verdict, the trend over runs and the recommended strategy. **Findings** lists everything with filters, triage and views by rule and by folder. **Modernization** compares strategies, effort, cost and the roadmap. **Report** opens the executive report and downloads the PDF and the SBOM.
4. Run again after changes: findings are reconciled by fingerprint, so the comparison shows resolved, new and regressed items and the health delta.

**No source at hand?** Click *Load demo data* on the empty dashboard: five fictional assessments with findings, scores, trend and reports appear instantly and can be removed with one click.

**A verdict of "nothing to assess"** means no analyzable source was found in the location — check the folder, the scope filters and the [supported languages](/help#findings). It is not a perfect score.`,
      "pt-BR": `1. Clique em **Nova avaliação** e escolha a fonte: uma pasta sob uma das raízes montadas (seletor de pastas), uma pasta da sua máquina (zipada no navegador e enviada; \`bin/\`, \`obj/\`, \`node_modules/\` e \`.git/\` são ignorados) ou uma URL git / GitHub / Azure DevOps / GitLab. Repositórios privados precisam de uma [credencial](/help#credentials) armazenada.
2. A execução entra na fila e a página acompanha ao vivo. Um repositório de algumas centenas de arquivos leva de segundos a poucos minutos.
3. **Visão geral** mostra o score com veredito, a tendência entre execuções e a estratégia recomendada. **Findings** lista tudo com filtros, triagem e visões por regra e por pasta. **Modernização** compara estratégias, esforço, custo e o roadmap. **Relatório** abre o relatório executivo e baixa o PDF e o SBOM.
4. Rode de novo após mudanças: os findings são reconciliados por fingerprint, então a comparação mostra resolvidos, novos e regredidos, além do delta de saúde.

**Sem código à mão?** Clique em *Carregar dados demo* no painel vazio: cinco avaliações fictícias com findings, scores, tendência e relatórios aparecem na hora e saem com um clique.

**Um veredito de "nada a avaliar"** significa que nenhuma fonte analisável foi encontrada no local — verifique a pasta, os filtros de escopo e as [linguagens suportadas](/help#findings). Não é um score perfeito.`,
    },
  },
  {
    id: "navigation",
    group: "start",
    title: { en: "Getting around the UI", "pt-BR": "Navegando pela interface" },
    body: {
      en: `- **Language**: the *Português / English* button in the sidebar switches the UI; reports and AI answers follow the reader's language too (\`?lang=en|pt-BR\` on report URLs).
- **Theme**: light, dark or system, in the sidebar footer. \`?theme=dark\` on any URL forces a theme for screenshots.
- **Deep links**: tabs are in the URL (\`?tab=findings\`, \`/ai-estate?tab=budgets\`), so a link opens exactly what you were looking at.
- **Presentation mode** hides the chrome for meetings; *Esc* or the exit button restores it.
- **Contextual help**: the **?** button in a page header opens this manual at the matching section.
- **After an upgrade**, if the version in the sidebar changed but the UI looks old, press *Ctrl+F5* once (the shell is served uncacheable since v0.54.1, so this is only needed when coming from an older version).
- **Keyboard**: every control is reachable by *Tab*; tabs are real \`role=tab\` elements for screen readers.`,
      "pt-BR": `- **Idioma**: o botão *Português / English* na barra lateral troca a interface; relatórios e respostas da IA também seguem o idioma do leitor (\`?lang=en|pt-BR\` nas URLs de relatório).
- **Tema**: claro, escuro ou do sistema, no rodapé da barra lateral. \`?theme=dark\` em qualquer URL força um tema para capturas de tela.
- **Links profundos**: as abas ficam na URL (\`?tab=findings\`, \`/ai-estate?tab=budgets\`), então um link abre exatamente o que você estava vendo.
- **Modo apresentação** esconde a moldura para reuniões; *Esc* ou o botão de sair restaura.
- **Ajuda contextual**: o botão **?** no cabeçalho de uma página abre este manual na seção correspondente.
- **Depois de um upgrade**, se a versão na barra lateral mudou mas a interface parece antiga, pressione *Ctrl+F5* uma vez (a shell é servida sem cache desde a v0.54.1, então isso só é necessário vindo de uma versão mais antiga).
- **Teclado**: todo controle é alcançável por *Tab*; as abas são elementos \`role=tab\` de verdade para leitores de tela.`,
    },
  },

  // ───────────────────────────── Assessments ─────────────────────────────
  {
    id: "sources",
    group: "assessments",
    routes: ["/assessments"],
    title: { en: "Sources: folders, uploads and git", "pt-BR": "Fontes: pastas, uploads e git" },
    body: {
      en: `| Kind | Locator | Notes |
|---|---|---|
| local | \`/sources/<folder>\` | Up to three host folders mounted read-only (\`ATLAS_LOCAL_SOURCES\`, \`_2\`, \`_3\`). The picker browses them one level at a time and refuses paths outside the roots. Folders are only read, never written. |
| upload | created by the UI | The browser zips the folder and uploads it; the archive lives on the \`atlas-uploads\` volume with zip-slip protection and size caps. Re-upload keeps the history. Orphan archives are garbage-collected after 24 h. |
| git | any clone URL | Shallow clone; \`Atlas:Connectors:Git:HistoryMonths\` adds history for churn analysis. Credentials go to git through the environment, never on the command line or in the URL. URLs with embedded credentials are refused. |
| github | \`owner\` or \`owner/repo\` | *Discover* lists repositories to create assessments in batch. GitHub Enterprise Server via \`Atlas:Connectors:GitHub:{ApiBaseUrl,WebBaseUrl}\`. |
| azure-devops | \`org/project[/repo]\` | Same discovery flow; on-premises server via \`Atlas:Connectors:AzureDevOps:BaseUrl\`. |
| gitlab | \`group[/subgroup[/project]]\` | Subgroups included; self-managed via \`Atlas:Connectors:GitLab:BaseUrl\`. |

**Which hosts may the worker clone from?** \`Atlas:Connectors:Git:AllowedHosts\` is the egress allow-list. Everything else is refused before a clone starts.

**Scope.** An \`.atlasignore\` at the repository root (gitignore syntax) and the per-assessment *exclude paths* (Settings tab) remove folders from analysis on the next run. Vendored, minified and generated code is excluded by default.`,
      "pt-BR": `| Tipo | Locator | Observações |
|---|---|---|
| local | \`/sources/<pasta>\` | Até três pastas do host montadas somente leitura (\`ATLAS_LOCAL_SOURCES\`, \`_2\`, \`_3\`). O seletor navega um nível por vez e recusa caminhos fora das raízes. As pastas são só lidas, nunca escritas. |
| upload | criado pela UI | O navegador zipa a pasta e envia; o arquivo fica no volume \`atlas-uploads\` com proteção contra zip-slip e limites de tamanho. Reenviar mantém o histórico. Arquivos órfãos são coletados após 24 h. |
| git | qualquer URL de clone | Clone raso; \`Atlas:Connectors:Git:HistoryMonths\` adiciona histórico para a análise de churn. Credenciais vão ao git pelo ambiente, nunca na linha de comando nem na URL. URLs com credenciais embutidas são recusadas. |
| github | \`owner\` ou \`owner/repo\` | *Descobrir* lista repositórios para criar avaliações em lote. GitHub Enterprise Server via \`Atlas:Connectors:GitHub:{ApiBaseUrl,WebBaseUrl}\`. |
| azure-devops | \`org/projeto[/repo]\` | Mesmo fluxo de descoberta; servidor on-premises via \`Atlas:Connectors:AzureDevOps:BaseUrl\`. |
| gitlab | \`grupo[/subgrupo[/projeto]]\` | Subgrupos incluídos; self-managed via \`Atlas:Connectors:GitLab:BaseUrl\`. |

**De quais hosts o worker pode clonar?** \`Atlas:Connectors:Git:AllowedHosts\` é a lista de saída permitida. Tudo o mais é recusado antes de o clone começar.

**Escopo.** Um \`.atlasignore\` na raiz do repositório (sintaxe do gitignore) e os *caminhos excluídos* por avaliação (aba Configurações) removem pastas da análise na próxima execução. Código vendorizado, minificado e gerado é excluído por padrão.`,
    },
  },
  {
    id: "assessment-overview",
    group: "assessments",
    routes: ["/assessments/:id"],
    title: { en: "Overview, health score and verdict", "pt-BR": "Visão geral, health score e veredito" },
    body: {
      en: `The **Overview** tab reads like page one of the report and is live while a run is in progress.

### Health score (\`health.v1\`)
Five dimensions, each 0–100 with a weight: **Security** (includes secrets and personal data), **Modernization**, **Dependencies**, **Architecture**, **Quality**. Every point lost is attributed to a rule and a count, so the number is never a mystery. Risk bands: Critical below 40, High below 60, Medium below 80, Low otherwise.

### Verdict
A sentence that states what the numbers mean for this system, followed by the tiles that matter (open findings by severity, blockers, coverage, size) and *what changed* since the previous run.

### Trend
The score over every completed run. Targets set on the Settings tab (a score and a date) show as met, on track, at risk or missed.

### Recommended strategy
The best-fitting modernization strategy with its rationale; details live on the [Modernization](/help#modernization) tab.

Suppressed and false-positive findings do not count. Reopening a waiver recomputes the score immediately.`,
      "pt-BR": `A aba **Visão geral** se lê como a primeira página do relatório e fica ao vivo enquanto uma execução está em andamento.

### Health score (\`health.v1\`)
Cinco dimensões, cada uma de 0 a 100 com um peso: **Segurança** (inclui segredos e dados pessoais), **Modernização**, **Dependências**, **Arquitetura**, **Qualidade**. Cada ponto perdido é atribuído a uma regra e a uma contagem, então o número nunca é um mistério. Faixas de risco: Crítico abaixo de 40, Alto abaixo de 60, Médio abaixo de 80, Baixo acima disso.

### Veredito
Uma frase que diz o que os números significam para este sistema, seguida dos indicadores que importam (findings abertos por severidade, bloqueadores, cobertura, tamanho) e do *que mudou* desde a execução anterior.

### Tendência
O score em todas as execuções concluídas. Metas definidas na aba Configurações (um score e uma data) aparecem como atingida, no caminho, em risco ou perdida.

### Estratégia recomendada
A estratégia de modernização de melhor aderência com sua justificativa; os detalhes ficam na aba [Modernização](/help#modernization).

Findings suprimidos e falsos positivos não contam. Reabrir um waiver recalcula o score imediatamente.`,
    },
  },
  {
    id: "findings",
    group: "assessments",
    title: { en: "Findings, categories and triage", "pt-BR": "Findings, categorias e triagem" },
    body: {
      en: `### What the scanners look at

| Scanner | Rules | Looks at |
|---|---|---|
| Dependencies | \`dependency.*\` | NuGet and npm (lockfiles v1–v3) against OSV; end-of-life target frameworks; migration blockers (System.Web, WCF, Remoting, WF, MSMQ, legacy project format, EF6…) |
| Security | \`sec.*\` | SQL built by concatenation, weak hashes, unsafe deserialization, debug/trace in production configuration… |
| Secrets | \`secrets.*\` | Connection strings with passwords, API keys, private key material, tokens in code and configuration (fingerprinted, never stored) |
| Personal data | \`privacy.*\` | Identifiers (CPF/CNPJ/passport…), contact, financial, health, credential and birth data; those values flowing into logs or exceptions |
| Quality | \`quality.*\` | Complexity, duplication, absent tests, coverage reports, discouraged APIs, \`[Obsolete]\`, dead-code candidates |
| Architecture | \`architecture.*\` | Project cycles, fan-out, change hotspots and knowledge silos from git history |
| Database | \`database.*\` | DDL, EF migrations, EDMX; dynamic SQL, cursors, \`SELECT *\`; personal data in column names |
| Infrastructure | \`infra.*\` | Dockerfiles, compose files and production \`appsettings*.json\` |
| JavaScript | \`javascript.*\` | Front-end frameworks and end-of-life ones, \`eval\`, DOM injection, plain-HTTP calls |
| Java | \`java.*\` | Maven/Gradle inventory, JDK out of support, EOL frameworks, \`javax\`→\`jakarta\` surface, OSV CVEs (opt-in bundle) |
| Python | \`python.*\` | requirements/pyproject/Pipfile/setup.py inventory, interpreter floors, EOL frameworks, OSV CVEs (opt-in bundle) |
| AI Estate | \`ai.*\` | Provider SDKs, agent frameworks, endpoints, retired models, MCP configs, unapproved providers — see [AI Estate](/help#ai-estate) |
| Licenses | \`license.*\` | Every dependency classified from registry metadata; a deny list turns matches into Critical findings; feeds the SBOM |

Languages analyzed today: C# (Roslyn, semantic where symbols resolve), VB.NET, SQL, JavaScript/TypeScript, Java and Python (syntactic). Others are inventoried by file count.

### The Findings tab
- **Filters** by severity, category, scanner, status and free text; **views** *list*, *by rule* (what) and *by folder* (where, a heat map).
- Every finding shows rule, severity, confidence, location, message and remediation. **Confidence** is the scanner's certainty; **severity** is the impact, tunable per tenant on the [Rules](/help#rules) page.
- **Triage** on a finding: *Suppress* (accepted, with reason, author and optional expiry), *False positive*, *Reopen*. Every decision is audited and recomputes the score; see [Waivers](/help#waivers).
- **Explain with AI** and **Suggest a fix** appear when an administrator has enabled an [AI provider](/help#ai-settings).
- **Fingerprints** identify a finding across runs (rule, location, normalized content; never severity), which is how *new*, *resolved* and *regressed* are computed.`,
      "pt-BR": `### O que os scanners olham

| Scanner | Regras | Olha para |
|---|---|---|
| Dependências | \`dependency.*\` | NuGet e npm (lockfiles v1–v3) contra o OSV; target frameworks fora de suporte; bloqueadores de migração (System.Web, WCF, Remoting, WF, MSMQ, formato legado de projeto, EF6…) |
| Segurança | \`sec.*\` | SQL montado por concatenação, hashes fracos, desserialização inseguras, debug/trace em configuração de produção… |
| Segredos | \`secrets.*\` | Connection strings com senha, chaves de API, material de chave privada, tokens em código e configuração (fingerprint, nunca armazenados) |
| Dados pessoais | \`privacy.*\` | Identificadores (CPF/CNPJ/passaporte…), contato, financeiro, saúde, credenciais e nascimento; esses valores indo para logs ou exceções |
| Qualidade | \`quality.*\` | Complexidade, duplicação, ausência de testes, relatórios de cobertura, APIs desencorajadas, \`[Obsolete]\`, candidatos a código morto |
| Arquitetura | \`architecture.*\` | Ciclos entre projetos, fan-out, hotspots de mudança e silos de conhecimento a partir do histórico git |
| Banco de dados | \`database.*\` | DDL, migrations EF, EDMX; SQL dinâmico, cursores, \`SELECT *\`; dados pessoais em nomes de coluna |
| Infraestrutura | \`infra.*\` | Dockerfiles, arquivos compose e \`appsettings*.json\` de produção |
| JavaScript | \`javascript.*\` | Frameworks de front end e os fora de suporte, \`eval\`, injeção no DOM, chamadas HTTP sem TLS |
| Java | \`java.*\` | Inventário Maven/Gradle, JDK fora de suporte, frameworks EOL, superfície \`javax\`→\`jakarta\`, CVEs do OSV (bundle opcional) |
| Python | \`python.*\` | Inventário requirements/pyproject/Pipfile/setup.py, versão mínima do interpretador, frameworks EOL, CVEs do OSV (bundle opcional) |
| AI Estate | \`ai.*\` | SDKs de provedores, frameworks de agentes, endpoints, modelos aposentados, configs MCP, provedores não aprovados — veja [AI Estate](/help#ai-estate) |
| Licenças | \`license.*\` | Cada dependência classificada a partir dos metadados do registry; uma lista de negação transforma ocorrências em findings Críticos; alimenta o SBOM |

Linguagens analisadas hoje: C# (Roslyn, semântico onde os símbolos resolvem), VB.NET, SQL, JavaScript/TypeScript, Java e Python (sintático). As demais são inventariadas por contagem de arquivos.

### A aba Findings
- **Filtros** por severidade, categoria, scanner, status e texto livre; **visões** *lista*, *por regra* (o quê) e *por pasta* (onde, um mapa de calor).
- Cada finding mostra regra, severidade, confiança, localização, mensagem e remediação. **Confiança** é a certeza do scanner; **severidade** é o impacto, ajustável por tenant na página [Regras](/help#rules).
- **Triagem** num finding: *Suprimir* (aceito, com motivo, autor e validade opcional), *Falso positivo*, *Reabrir*. Toda decisão é auditada e recalcula o score; veja [Waivers](/help#waivers).
- **Explicar com IA** e **Sugerir correção** aparecem quando um administrador ativou um [provedor de IA](/help#ai-settings).
- **Fingerprints** identificam um finding entre execuções (regra, localização, conteúdo normalizado; nunca a severidade), e é assim que *novo*, *resolvido* e *regredido* são calculados.`,
    },
  },
  {
    id: "waivers",
    group: "assessments",
    title: { en: "Waivers, policies and expiry", "pt-BR": "Waivers, políticas e validade" },
    body: {
      en: `A waiver is a human decision about a finding. The **Waivers** tab of an assessment gathers all of them.

- **Suppression** (accepted risk) or **false positive**, with reason and author. An optional **expiry** ("accepted for 90 days") reopens the finding by itself when the date passes and recomputes the score — accepted risks cannot silently become permanent.
- **Policies** suppress by rule and optional path pattern, per assessment or tenant-wide (tenant-wide ones are created through the API by an administrator and listed here). They are expirable too.
- **History** keeps expired and reopened waivers, so an audit can see what was accepted, by whom, and when it ended.
- **Rule major bumps.** When a rule changes meaning, its major version is bumped and existing waivers are never carried over silently. The tab lists the superseded waivers and lets you migrate only the ones you choose, each as a new audited decision with the original kind and expiry.
- The **compliance pack** (Report tab) exports every waiver with reason, author and expiry alongside privacy findings and the license inventory.

Waivers never delete data: the finding stays with status *Suppressed* and is excluded from the score and the gate.`,
      "pt-BR": `Um waiver é uma decisão humana sobre um finding. A aba **Waivers** de uma avaliação reúne todas elas.

- **Supressão** (risco aceito) ou **falso positivo**, com motivo e autor. Uma **validade** opcional ("aceito por 90 dias") reabre o finding sozinha quando a data passa e recalcula o score — riscos aceitos não podem virar permanentes em silêncio.
- **Políticas** suprimem por regra e padrão de caminho opcional, por avaliação ou para o tenant inteiro (as do tenant são criadas pela API por um administrador e listadas aqui). Também expiram.
- **Histórico** guarda waivers expirados e reabertos, então uma auditoria vê o que foi aceito, por quem e quando terminou.
- **Bumps de versão maior de regra.** Quando uma regra muda de significado, sua versão maior sobe e os waivers existentes nunca são carregados em silêncio. A aba lista os waivers substituídos e deixa migrar só os que você escolher, cada um como uma nova decisão auditada com o tipo e a validade originais.
- O **pacote de compliance** (aba Relatório) exporta todo waiver com motivo, autor e validade junto com os findings de privacidade e o inventário de licenças.

Waivers nunca apagam dados: o finding permanece com status *Suprimido* e fica fora do score e do gate.`,
    },
  },
  {
    id: "runs",
    group: "assessments",
    title: { en: "Runs, comparison and schedule", "pt-BR": "Execuções, comparação e agenda" },
    body: {
      en: `- **Run again** queues a new run; while one is queued or in progress the button answers 409. Runs are executed by the worker in a disposable child process with memory and time limits; a crash fails that run, not the worker.
- The **Runs** tab lists every run with duration, per-scanner outcome and findings per run; pick two runs to **compare**: resolved, new and regressed findings and the health delta.
- **Schedule** (Settings tab) re-assesses on a cadence (daily, weekly, monthly) and can call a **webhook** when a run completes. Webhooks carry scores and counts, never finding contents, and are HMAC-SHA256 signed.
- **Slack / Teams** cards for completed runs and the optional weekly digest are configured under [Administration](/help#admin).
- Every run records the version of Atlas, the rule catalog and (for AI findings) the signature catalog hash it used, so results are reproducible.`,
      "pt-BR": `- **Executar de novo** enfileira uma nova execução; enquanto uma está na fila ou em andamento o botão responde 409. As execuções rodam no worker num processo filho descartável com limites de memória e tempo; um crash derruba aquela execução, não o worker.
- A aba **Execuções** lista cada execução com duração, resultado por scanner e findings por execução; escolha duas para **comparar**: findings resolvidos, novos e regredidos e o delta de saúde.
- **Agenda** (aba Configurações) reavalia numa cadência (diária, semanal, mensal) e pode chamar um **webhook** quando a execução termina. Webhooks levam scores e contagens, nunca conteúdo de findings, e são assinados com HMAC-SHA256.
- Cards para **Slack / Teams** de execuções concluídas e o resumo semanal opcional são configurados em [Administração](/help#admin).
- Cada execução registra a versão do Atlas, do catálogo de regras e (para findings de IA) o hash do catálogo de assinaturas usado, então os resultados são reproduzíveis.`,
    },
  },
  {
    id: "modernization",
    group: "assessments",
    title: { en: "Modernization: strategies, cost, savings, roadmap", "pt-BR": "Modernização: estratégias, custo, economia, roadmap" },
    body: {
      en: `### Strategies (\`modernization.v1\`)
Six options — keep & stabilize, upgrade in place, incremental, strangler, partial rewrite, full rewrite — get additive **fit scores** from framework generation, blockers by weight, UI frameworks without an upgrade path, test posture, size, coupling and security debt. Each lists rationale, prerequisites, blockers and benefits.

### Cost (\`cost.v1\`)
Base effort per KLOC by strategy, plus blockers and security debt, times explicit multipliers, widened by confidence into **optimistic / likely / conservative** hours, months and money. Parameters live in \`Atlas:Cost:*\`; currency, hourly rate and team size can be overridden per tenant in [Settings → Cost model](/help#cost-settings). There is deliberately no FX conversion: an hourly rate is a market fact.

### What staying legacy costs (\`savings.v1\`)
The annual run-rate of not modernizing — Windows hosting, extended support exposure, SQL Server consolidation — and the **payback** per strategy. Both sides of the decision in the same view and report.

### Roadmap (\`roadmap.v1\`)
Baseline → security → characterization tests → foundation (SDK-style, PackageReference, target framework) → domain migration → data/integrations → retirement — only the phases the evidence calls for, with effort shares and dependencies, shown as a Gantt.

### Actuals and calibration
Record what a modernization really took (hours, months, cost) on the Modernization tab. The portfolio's **calibration** compares actuals with estimates across assessments and tells you whether the rates should move.

### Migration plan (AI, optional)
With an AI provider enabled, *Draft migration plan* writes a plan from the estimate's own facts; it exports as Markdown for wikis and pull requests.`,
      "pt-BR": `### Estratégias (\`modernization.v1\`)
Seis opções — manter e estabilizar, atualizar no lugar, incremental, strangler, reescrita parcial, reescrita total — recebem **scores de aderência** aditivos a partir da geração do framework, bloqueadores por peso, frameworks de UI sem caminho de upgrade, postura de testes, tamanho, acoplamento e dívida de segurança. Cada uma lista justificativa, pré-requisitos, bloqueadores e benefícios.

### Custo (\`cost.v1\`)
Esforço base por KLOC por estratégia, mais bloqueadores e dívida de segurança, vezes multiplicadores explícitos, alargado pela confiança em horas, meses e dinheiro **otimista / provável / conservador**. Os parâmetros ficam em \`Atlas:Cost:*\`; moeda, valor-hora e tamanho do time podem ser sobrescritos por tenant em [Configurações → Modelo de custo](/help#cost-settings). Não há conversão de câmbio de propósito: valor-hora é um fato de mercado.

### Quanto custa ficar no legado (\`savings.v1\`)
O custo anual de não modernizar — hospedagem Windows, exposição a suporte estendido, consolidação de SQL Server — e o **payback** por estratégia. Os dois lados da decisão na mesma tela e no relatório.

### Roadmap (\`roadmap.v1\`)
Baseline → segurança → testes de caracterização → fundação (SDK-style, PackageReference, target framework) → migração de domínio → dados/integrações → desativação — só as fases que a evidência pede, com fatias de esforço e dependências, num Gantt.

### Realizados e calibração
Registre quanto uma modernização realmente custou (horas, meses, valor) na aba Modernização. A **calibração** do portfólio compara realizados com estimativas entre avaliações e diz se as taxas devem mudar.

### Plano de migração (IA, opcional)
Com um provedor de IA ativo, *Rascunhar plano de migração* escreve um plano a partir dos fatos da própria estimativa; ele exporta em Markdown para wikis e pull requests.`,
    },
  },
  {
    id: "report",
    group: "assessments",
    title: { en: "Reports and exports", "pt-BR": "Relatórios e exportações" },
    body: {
      en: `| What | Where |
|---|---|
| Executive report (HTML) | Report tab, or \`GET /api/assessments/{id}/report?lang=en|pt-BR\`. Page one with verdict, tiles, what changed and top risks; then health, coverage, key risks, modernization, business rules, findings by category, inventory, per-project table, capped appendix. \`?since=\` (a picker on the tab) compares against an older baseline. |
| PDF | \`…/report.pdf\`, rendered by the Gotenberg sidecar; footer with brand and page numbers. White-label with \`Atlas:Report:{BrandName,PreparedBy,LogoDataUri,AccentColor}\`. |
| Portfolio report | \`GET /api/portfolio/report[.pdf]?lang=&tag=&weeks=\` — the whole estate or one product group. |
| Findings | \`…/findings/export?format=csv|json|sarif\` (SARIF 2.1.0 for GitHub / Azure DevOps code scanning). |
| SBOM | \`…/sbom\` — CycloneDX 1.5 with purls and license expressions. |
| Business rules | \`…/business-rules/export?format=csv|json\`. |
| Migration plan | \`…/migration-plan/export\` — Markdown. |
| Compliance pack | \`…/compliance.zip\` — privacy findings, license inventory and every waiver with reason, author and expiry. |
| Issues | *Create issues*: the top open findings become GitHub / Azure DevOps issues on the assessed repository, using its stored credential. |
| AI summary | With a provider enabled, *Write executive summary* adds a model-written page-one summary from the report's own figures, labelled and dated. |

Downloads are also available through the API with an [API token](/help#tokens).`,
      "pt-BR": `| O quê | Onde |
|---|---|
| Relatório executivo (HTML) | Aba Relatório, ou \`GET /api/assessments/{id}/report?lang=en|pt-BR\`. Primeira página com veredito, indicadores, o que mudou e principais riscos; depois saúde, cobertura, riscos-chave, modernização, regras de negócio, findings por categoria, inventário, tabela por projeto, apêndice limitado. \`?since=\` (um seletor na aba) compara com uma baseline mais antiga. |
| PDF | \`…/report.pdf\`, renderizado pelo sidecar Gotenberg; rodapé com marca e numeração. White-label com \`Atlas:Report:{BrandName,PreparedBy,LogoDataUri,AccentColor}\`. |
| Relatório de portfólio | \`GET /api/portfolio/report[.pdf]?lang=&tag=&weeks=\` — o parque inteiro ou um grupo de produto. |
| Findings | \`…/findings/export?format=csv|json|sarif\` (SARIF 2.1.0 para code scanning do GitHub / Azure DevOps). |
| SBOM | \`…/sbom\` — CycloneDX 1.5 com purls e expressões de licença. |
| Regras de negócio | \`…/business-rules/export?format=csv|json\`. |
| Plano de migração | \`…/migration-plan/export\` — Markdown. |
| Pacote de compliance | \`…/compliance.zip\` — findings de privacidade, inventário de licenças e todo waiver com motivo, autor e validade. |
| Issues | *Criar issues*: os principais findings abertos viram issues no GitHub / Azure DevOps do repositório avaliado, usando a credencial armazenada. |
| Resumo por IA | Com um provedor ativo, *Escrever resumo executivo* adiciona um resumo de primeira página escrito pelo modelo a partir dos próprios números do relatório, rotulado e datado. |

Os downloads também estão disponíveis pela API com um [token de API](/help#tokens).`,
    },
  },
  {
    id: "assessment-settings",
    group: "assessments",
    title: { en: "Assessment settings: scope, tags, sharing, targets, SARIF import", "pt-BR": "Configurações da avaliação: escopo, tags, compartilhamento, metas, import SARIF" },
    body: {
      en: `- **Rename** the assessment; the source locator stays.
- **Scope**: gitignore-like patterns excluded on the next run, on top of the defaults and the repository's \`.atlasignore\`.
- **Tags**: free-form labels used by the portfolio for grouping, filtering and the per-group report.
- **Target**: a health score and a date; the portfolio reports met / on track / at risk / missed.
- **Schedule and webhook**: see [Runs](/help#runs).
- **Sharing** (when sign-in is on): an assessment can be restricted to an access list of Viewers, Editors and Owners; hidden assessments are simply absent (404) for everyone else. Owners and tenant administrators manage the list.
- **Import SARIF**: upload a SARIF 2.1.0 log from ESLint, Semgrep, Trivy, CodeQL… Each tool becomes its own scanner (\`external.<tool>\`): its findings are triaged, suppressed and scored like Atlas's own, and a later import resolves what the tool no longer reports.
- **Re-upload** (upload assessments): point the assessment at a new archive and queue a run; history is kept.
- **Delete** removes the assessment, its runs and findings; upload archives are garbage-collected afterwards.`,
      "pt-BR": `- **Renomear** a avaliação; o locator da fonte permanece.
- **Escopo**: padrões estilo gitignore excluídos na próxima execução, além dos padrões e do \`.atlasignore\` do repositório.
- **Tags**: rótulos livres usados pelo portfólio para agrupar, filtrar e gerar o relatório por grupo.
- **Meta**: um health score e uma data; o portfólio reporta atingida / no caminho / em risco / perdida.
- **Agenda e webhook**: veja [Execuções](/help#runs).
- **Compartilhamento** (com login ativo): uma avaliação pode ser restrita a uma lista de acesso com Leitores, Editores e Donos; avaliações ocultas simplesmente não existem (404) para os demais. Donos e administradores do tenant gerenciam a lista.
- **Importar SARIF**: envie um log SARIF 2.1.0 do ESLint, Semgrep, Trivy, CodeQL… Cada ferramenta vira um scanner próprio (\`external.<ferramenta>\`): seus findings são triados, suprimidos e pontuados como os do Atlas, e um import posterior resolve o que a ferramenta não reporta mais.
- **Reenviar** (avaliações por upload): aponte a avaliação para um novo arquivo e enfileire uma execução; o histórico é mantido.
- **Excluir** remove a avaliação, suas execuções e findings; arquivos de upload são coletados depois.`,
    },
  },
  {
    id: "ai-assist",
    group: "assessments",
    title: { en: "AI assistance inside an assessment", "pt-BR": "Assistência de IA dentro de uma avaliação" },
    body: {
      en: `Available only after an administrator enables a provider under [Settings → AI](/help#ai-settings). Until then nothing is sent anywhere.

| Feature | What the model receives | Where |
|---|---|---|
| Explain with AI | Rule title, description, remediation, the finding's message and location — **no source code** | any finding |
| Suggest a fix | About 50 line-numbered lines around the finding with credential values masked; **never** for secrets findings, binaries or findings without a location. Returns diagnosis, a unified diff and notes | finding triage bar (runs as a worker job) |
| Analyze business rules | The source of the most decision-heavy methods, in batches, capped per analysis; tests, generated code, migrations and DTOs skipped. Returns rules with name, description (EN + PT), category, conditions, confidence and origin | Business rules tab |
| Write executive summary | The report's own figures only | Report tab |
| Draft migration plan | Estate profile, strategy rationale, estimate with assumptions, roadmap | Modernization tab |
| PR note | The run comparison and gate result | \`pr-comment?ai=true\` |

Every answer is cached per language, labelled with the model and dated; token usage is recorded and shown under Settings → AI. **AI output never changes scores or findings.** Readers vote 👍/👎 with an optional comment; the perceived quality per feature and per model is shown to administrators so the cost of a provider can be weighed against usefulness.`,
      "pt-BR": `Disponível só depois que um administrador ativa um provedor em [Configurações → IA](/help#ai-settings). Até então nada é enviado a lugar nenhum.

| Recurso | O que o modelo recebe | Onde |
|---|---|---|
| Explicar com IA | Título, descrição e remediação da regra, a mensagem e a localização do finding — **nenhum código-fonte** | qualquer finding |
| Sugerir correção | Cerca de 50 linhas numeradas ao redor do finding com valores de credenciais mascarados; **nunca** para findings de segredos, binários ou findings sem localização. Retorna diagnóstico, um diff unificado e notas | barra de triagem do finding (roda como job do worker) |
| Analisar regras de negócio | O código dos métodos com mais decisões, em lotes, com teto por análise; testes, código gerado, migrations e DTOs são pulados. Retorna regras com nome, descrição (EN + PT), categoria, condições, confiança e origem | aba Regras de negócio |
| Escrever resumo executivo | Só os números do próprio relatório | aba Relatório |
| Rascunhar plano de migração | Perfil do parque, justificativa da estratégia, estimativa com premissas, roadmap | aba Modernização |
| Nota de PR | A comparação da execução e o resultado do gate | \`pr-comment?ai=true\` |

Toda resposta é cacheada por idioma, rotulada com o modelo e datada; o uso de tokens é registrado e mostrado em Configurações → IA. **A saída da IA nunca muda scores ou findings.** Leitores votam 👍/👎 com comentário opcional; a qualidade percebida por recurso e por modelo é mostrada aos administradores para pesar o custo de um provedor contra a utilidade.`,
    },
  },

  // ───────────────────────────── Portfolio ─────────────────────────────
  {
    id: "dashboard",
    group: "portfolio",
    routes: ["/"],
    title: { en: "Dashboard", "pt-BR": "Painel" },
    body: {
      en: `The home page is the portfolio at a glance: risk distribution, open findings by severity and category, a **needs attention** list, legacy versus modern frameworks, and one card per assessment with score, delta and status.

Two cards summarize the governance module: **AI estate** (providers, unapproved ones, repositories with AI next to personal data or secrets) and **AI spend** (month to date, projected month, developers reporting, budgets at risk or over). Both link to the [AI estate](/help#ai-estate) page.

On an empty instance the dashboard offers *Load demo data*: five fictional assessments to explore every screen; *Remove demo data* deletes them again.`,
      "pt-BR": `A página inicial é o portfólio de relance: distribuição de risco, findings abertos por severidade e categoria, uma lista de **precisa de atenção**, frameworks legados versus modernos e um card por avaliação com score, delta e status.

Dois cards resumem o módulo de governança: **AI estate** (provedores, não aprovados, repositórios com IA ao lado de dados pessoais ou segredos) e **Gasto com IA** (mês até agora, mês projetado, desenvolvedores reportando, orçamentos em risco ou estourados). Ambos levam à página [AI estate](/help#ai-estate).

Numa instância vazia o painel oferece *Carregar dados demo*: cinco avaliações fictícias para explorar todas as telas; *Remover dados demo* as apaga de novo.`,
    },
  },
  {
    id: "portfolio",
    group: "portfolio",
    routes: ["/portfolio"],
    title: { en: "Portfolio, benchmark, tags and trend", "pt-BR": "Portfólio, benchmark, tags e tendência" },
    body: {
      en: `Every system side by side.

- **Risk distribution** and **KPIs** for the estate or for one tag (product group); filter by tag at the top.
- **Benchmark**: quartiles (P25 / P50 / P75) per dimension with each assessment's percentile — where a system sits relative to its peers.
- **Targets**: met, on track, at risk or missed, per assessment.
- **Top rules** across the estate — what to fix first for the largest effect.
- **Trend**: average health and open findings week by week, recomputed from the runs that already happened; \`?weeks=\` and \`?tag=\` on the API.
- **Calibration**: recorded actuals versus estimates, and whether the cost rates should move.
- **Portfolio report** (HTML / PDF) turns the same view into a client-ready document, for the whole estate or one product group.`,
      "pt-BR": `Todos os sistemas lado a lado.

- **Distribuição de risco** e **KPIs** do parque ou de uma tag (grupo de produto); filtre por tag no topo.
- **Benchmark**: quartis (P25 / P50 / P75) por dimensão com o percentil de cada avaliação — onde um sistema está em relação aos pares.
- **Metas**: atingida, no caminho, em risco ou perdida, por avaliação.
- **Principais regras** do parque — o que corrigir primeiro para o maior efeito.
- **Tendência**: saúde média e findings abertos semana a semana, recalculados a partir das execuções que já aconteceram; \`?weeks=\` e \`?tag=\` na API.
- **Calibração**: realizados registrados versus estimativas, e se as taxas de custo devem mudar.
- **Relatório de portfólio** (HTML / PDF) transforma a mesma visão num documento pronto para o cliente, do parque inteiro ou de um grupo de produto.`,
    },
  },
  {
    id: "compare",
    group: "portfolio",
    routes: ["/compare"],
    title: { en: "Compare two assessments", "pt-BR": "Comparar duas avaliações" },
    body: {
      en: `Pick two assessments and see the same columns for both: health by dimension, open findings by severity, size, frameworks, blockers, recommended strategy and estimate. Both must be visible to you when sharing is in use.

To compare two **runs of the same assessment** (before and after a change), use the Runs tab of that assessment instead.`,
      "pt-BR": `Escolha duas avaliações e veja as mesmas colunas para ambas: saúde por dimensão, findings abertos por severidade, tamanho, frameworks, bloqueadores, estratégia recomendada e estimativa. As duas precisam estar visíveis para você quando o compartilhamento está em uso.

Para comparar duas **execuções da mesma avaliação** (antes e depois de uma mudança), use a aba Execuções daquela avaliação.`,
    },
  },
  {
    id: "rules",
    group: "portfolio",
    routes: ["/rules"],
    title: { en: "Rule catalog and severity tuning", "pt-BR": "Catálogo de regras e ajuste de severidade" },
    body: {
      en: `The **Rules** page lists every rule Atlas checks: id, scanner, category, description, default severity, and how much it fires in your estate (open findings and assessments affected).

**Severity tuning** (administrators): pick another severity for a rule and every open finding of that rule changes severity for this tenant, along with the health score. Finding history survives because fingerprints exclude severity. Choosing the default again removes the override.

Rule ids are stable and prefixed by scanner (\`sec.sql-concatenation\`, \`ai.unapproved-provider\`…). A rule's **version** follows MAJOR.MINOR.PATCH; a major bump means the rule changed meaning and triggers the [waiver migration](/help#waivers) flow.`,
      "pt-BR": `A página **Regras** lista toda regra que o Atlas verifica: id, scanner, categoria, descrição, severidade padrão e quanto ela dispara no seu parque (findings abertos e avaliações afetadas).

**Ajuste de severidade** (administradores): escolha outra severidade para uma regra e todo finding aberto dessa regra muda de severidade neste tenant, junto com o health score. O histórico dos findings sobrevive porque os fingerprints excluem a severidade. Escolher o padrão de novo remove o ajuste.

Ids de regra são estáveis e prefixados pelo scanner (\`sec.sql-concatenation\`, \`ai.unapproved-provider\`…). A **versão** de uma regra segue MAJOR.MINOR.PATCH; um bump de versão maior significa que a regra mudou de significado e aciona o fluxo de [migração de waivers](/help#waivers).`,
    },
  },
  {
    id: "jobs",
    group: "portfolio",
    routes: ["/jobs"],
    title: { en: "Job queue", "pt-BR": "Fila de jobs" },
    body: {
      en: `Every unit of background work is a job leased from a PostgreSQL queue: assessment runs, AI fix suggestions, business-rule analyses. The **Jobs** page follows them live (server-sent events, with polling as fallback).

- States: queued → running → completed, or failed. A job is retried a bounded number of times and then **dead-lettered** with its error kept; *Retry* puts it back in the queue.
- The worker waits for the database schema to be migrated by the API before it starts consuming (first boot of a fresh install), so a queued job during startup is normal.
- The **audit trail** (\`GET /api/audit\`) records every state-changing API call with who and when.`,
      "pt-BR": `Toda unidade de trabalho em segundo plano é um job alugado de uma fila no PostgreSQL: execuções de avaliação, sugestões de correção por IA, análises de regras de negócio. A página **Jobs** os acompanha ao vivo (server-sent events, com polling como alternativa).

- Estados: na fila → executando → concluído, ou falhou. Um job é repetido um número limitado de vezes e então vai para a **dead-letter** com o erro guardado; *Repetir* o devolve à fila.
- O worker espera o schema do banco ser migrado pela API antes de começar a consumir (primeiro boot de uma instalação nova), então um job na fila durante a subida é normal.
- A **trilha de auditoria** (\`GET /api/audit\`) registra toda chamada de API que muda estado, com quem e quando.`,
    },
  },

  // ───────────────────────────── AI Estate ─────────────────────────────
  {
    id: "ai-estate",
    group: "aiEstate",
    routes: ["/ai-estate", "/ai-estate?tab=overview"],
    title: { en: "AI Estate: inventory of the AI your code uses", "pt-BR": "AI Estate: inventário da IA que seu código usa" },
    body: {
      en: `Atlas reads what AI each repository actually uses — from code, manifests and configuration, against a versioned **signature catalog** ({providers} providers today) — and puts it in every report next to modernization, privacy and secrets.

### What the \`ai.*\` rules find
- Provider SDKs and agent frameworks in NuGet, npm, PyPI and Maven/Gradle manifests, or imported in code (\`ai.provider.sdk\`, \`ai.agent.framework\`).
- Provider API hosts in code or configuration (\`ai.provider.direct-endpoint\`) and local runtimes such as Ollama, LM Studio and vLLM (\`ai.local-runtime\`).
- Model references, with **retired models** flagged and the replacement named (\`ai.model.retired\`).
- **MCP servers** configured for coding assistants (\`.mcp.json\`, \`.cursor/mcp.json\`, \`.vscode/mcp.json\`, Claude, Gemini, Windsurf, Continue…): transport, a read/write/execute capability class, remote hosts (\`ai.mcp.remote-server\`) and credential-looking values committed in the config, fingerprinted and never stored (\`ai.mcp.secret-in-config\`).
- Providers outside your **approved list** (\`ai.unapproved-provider\`, High) — see [Approved providers](/help#allowlist).
- The correlation no traffic-based tool can see: external AI calls in a repository that also has open **personal-data or secrets** findings.

### The Inventory tab
Providers with approval status and evidence, agent frameworks, MCP servers, retired models, and a per-repository table with flags. The same facts appear in each assessment's **AI estate** tab, in the executive report and in the portfolio report (team attribution by tags only).

**Custom catalog**: \`Atlas:AiEstate:CatalogPath\` replaces the embedded \`ai-signatures.json\` with your own file (same schema, validated on load; the report records the catalog version and hash).

What Atlas deliberately does **not** do: no silent endpoint agent, no browser extension, no per-user shadow-AI tracking, no policy engine beyond the allowlist.`,
      "pt-BR": `O Atlas lê qual IA cada repositório realmente usa — a partir de código, manifests e configuração, contra um **catálogo de assinaturas** versionado ({providers} provedores hoje) — e coloca isso em todo relatório ao lado de modernização, privacidade e segredos.

### O que as regras \`ai.*\` encontram
- SDKs de provedores e frameworks de agentes em manifests NuGet, npm, PyPI e Maven/Gradle, ou importados no código (\`ai.provider.sdk\`, \`ai.agent.framework\`).
- Hosts de API de provedores em código ou configuração (\`ai.provider.direct-endpoint\`) e runtimes locais como Ollama, LM Studio e vLLM (\`ai.local-runtime\`).
- Referências a modelos, com **modelos aposentados** sinalizados e o substituto nomeado (\`ai.model.retired\`).
- **Servidores MCP** configurados para assistentes de código (\`.mcp.json\`, \`.cursor/mcp.json\`, \`.vscode/mcp.json\`, Claude, Gemini, Windsurf, Continue…): transporte, uma classe de capacidade leitura/escrita/execução, hosts remotos (\`ai.mcp.remote-server\`) e valores com cara de credencial commitados na config, com fingerprint e nunca armazenados (\`ai.mcp.secret-in-config\`).
- Provedores fora da sua **lista aprovada** (\`ai.unapproved-provider\`, Alto) — veja [Provedores aprovados](/help#allowlist).
- A correlação que nenhuma ferramenta de tráfego enxerga: chamadas a IA externa num repositório que também tem findings abertos de **dados pessoais ou segredos**.

### A aba Inventário
Provedores com status de aprovação e evidência, frameworks de agentes, servidores MCP, modelos aposentados e uma tabela por repositório com sinalizadores. Os mesmos fatos aparecem na aba **AI estate** de cada avaliação, no relatório executivo e no relatório de portfólio (atribuição a times só por tags).

**Catálogo customizado**: \`Atlas:AiEstate:CatalogPath\` substitui o \`ai-signatures.json\` embutido pelo seu próprio arquivo (mesmo schema, validado ao carregar; o relatório registra a versão e o hash do catálogo).

O que o Atlas **não** faz de propósito: nenhum agente silencioso de endpoint, nenhuma extensão de navegador, nenhum rastreio de shadow AI por usuário, nenhum motor de políticas além da lista aprovada.`,
    },
  },
  {
    id: "allowlist",
    group: "aiEstate",
    routes: ["/ai-estate?tab=allowlist"],
    title: { en: "Approved providers (allowlist)", "pt-BR": "Provedores aprovados (allowlist)" },
    body: {
      en: `The V0.5 policy is a plain allowlist. Administrators pick approved providers from the signature catalog on the **Approved providers** tab; it is saved per tenant and applied to every assessment on its **next run**. Any external provider found in a repository and not on the list raises \`ai.unapproved-provider\` (High) with the evidence.

- With an **empty** list the rule stays silent — Atlas never guesses what you approve.
- The deployment configuration is only the fallback when nothing is saved:

\`\`\`yaml
Atlas__AiEstate__ApprovedProviders__0: azure-openai
Atlas__AiEstate__ApprovedProviders__1: anthropic
\`\`\`

- Local runtimes (Ollama, LM Studio, vLLM) are not "external providers" and never trigger the rule.
- Changing the list does not rewrite history: findings from earlier runs keep the allowlist they were evaluated against.`,
      "pt-BR": `A política V0.5 é uma lista aprovada simples. Administradores escolhem os provedores aprovados a partir do catálogo de assinaturas na aba **Provedores aprovados**; ela é salva por tenant e aplicada a toda avaliação na **próxima execução**. Qualquer provedor externo encontrado num repositório e ausente da lista gera \`ai.unapproved-provider\` (Alto) com a evidência.

- Com a lista **vazia** a regra fica em silêncio — o Atlas nunca adivinha o que você aprova.
- A configuração do deployment é só o fallback quando nada foi salvo:

\`\`\`yaml
Atlas__AiEstate__ApprovedProviders__0: azure-openai
Atlas__AiEstate__ApprovedProviders__1: anthropic
\`\`\`

- Runtimes locais (Ollama, LM Studio, vLLM) não são "provedores externos" e nunca disparam a regra.
- Mudar a lista não reescreve o histórico: findings de execuções anteriores mantêm a lista contra a qual foram avaliados.`,
    },
  },
  {
    id: "budgets",
    group: "aiEstate",
    routes: ["/ai-estate?tab=budgets"],
    title: { en: "Budgets, alerts and teams", "pt-BR": "Orçamentos, alertas e times" },
    body: {
      en: `### Budgets
A monthly **estimated-spend** budget for the whole tenant, a team, a provider, a model, a developer or a tool. The bar shows spent so far and the month's projection; colours turn at 80 % and 100 %.

### Alerts
- Thresholds at **50 / 80 / 100 %** of a budget, each fired once per month.
- **Anomaly**: a day above 3× the 7-day median and at least 5 USD, once per day.
- Delivered to the tenant's webhook, Slack or Teams channels ([Administration](/help#admin)), or to a **team's own channels** when the budget is scoped to a team that has them. Delivery errors are stored on the alert and shown in the list.
- Evaluated after every ingest (CLI report, telemetry, Cursor sync) and after a budget is saved.

### Teams
A team groups developers (pseudonyms or labels, with \`*\` wildcards) and services (\`svc:<name>\`) into a cost centre. Spend, month to date and projection roll up per team; the **breakdown** card splits the whole company by team, provider and tool, and exports the CSV finance needs (one line per day, developer, team, tool, provider and model).

Budgets and teams are administrator operations. Everything here is in **USD list prices** from the [price catalog](/help#prices), calibrated by the billing ratio when [reconciliation](/help#spend) has enough overlap.`,
      "pt-BR": `### Orçamentos
Um orçamento mensal de **gasto estimado** para o tenant inteiro, um time, um provedor, um modelo, um desenvolvedor ou uma ferramenta. A barra mostra o gasto até agora e a projeção do mês; as cores mudam em 80 % e 100 %.

### Alertas
- Limiares em **50 / 80 / 100 %** do orçamento, cada um disparado uma vez por mês.
- **Anomalia**: um dia acima de 3× a mediana de 7 dias e de pelo menos 5 USD, uma vez por dia.
- Entregues nos canais de webhook, Slack ou Teams do tenant ([Administração](/help#admin)), ou nos **canais próprios do time** quando o orçamento é de um time que os tem. Erros de entrega ficam gravados no alerta e aparecem na lista.
- Avaliados após cada ingestão (relatório da CLI, telemetria, sync do Cursor) e após salvar um orçamento.

### Times
Um time agrupa desenvolvedores (pseudônimos ou rótulos, com curingas \`*\`) e serviços (\`svc:<nome>\`) num centro de custo. Gasto, mês até agora e projeção se acumulam por time; o card de **decomposição** divide a empresa inteira por time, provedor e ferramenta, e exporta o CSV que o financeiro precisa (uma linha por dia, desenvolvedor, time, ferramenta, provedor e modelo).

Orçamentos e times são operações de administrador. Tudo aqui está em **preços de lista em USD** do [catálogo de preços](/help#prices), calibrado pela razão da fatura quando a [reconciliação](/help#spend) tem sobreposição suficiente.`,
    },
  },
  {
    id: "usage",
    group: "aiEstate",
    routes: ["/ai-estate?tab=usage"],
    title: { en: "Developer usage: the atlas-agent CLI, live view and forecast", "pt-BR": "Uso por desenvolvedor: a CLI atlas-agent, visão ao vivo e projeção" },
    body: {
      en: `### The opt-in CLI
To see usage and estimated cost **per developer**, each developer runs \`atlas-agent\` on their own machine (single-file binaries for Windows, Linux and macOS are attached to every release). It reads the usage logs the coding tools already keep locally — Claude Code (\`~/.claude/projects\`) and Codex CLI (\`~/.codex/sessions\`) — aggregates per day and model, and posts **only counts**: tokens, requests and sessions. Never prompts, code, paths or project names.

\`\`\`bash
atlas-agent --server {origin} --token <ATLAS_TOKEN> --days 7 --dry-run   # preview, sends nothing
atlas-agent --server {origin} --token <ATLAS_TOKEN> --days 7             # send (analyst token)
atlas-agent ... --anonymous                                               # stable per-machine pseudonym instead of a name
atlas-agent install --server {origin} --token <ATLAS_TOKEN> --every 12h  # schedule with the OS scheduler
atlas-agent status | uninstall | watch
\`\`\`

- Re-running is idempotent: a day's line is replaced, never counted twice.
- \`install\` saves settings under \`~/.atlas-agent\` and registers a **per-user** schedule (Task Scheduler, launchd or cron). There is no service and no resident process: the scheduler starts the agent, it reports, it exits. The server cannot configure it.
- \`--every 5m\` keeps the Live panel current; \`watch\` keeps a terminal open and re-sends today whenever the transcripts change.
- Cursor, Copilot and Gemini CLI keep no token counts locally, so the agent says so instead of guessing; they arrive through [telemetry](/help#telemetry) or the Cursor [cost source](/help#spend).
- Each developer needs an **analyst** [API token](/help#tokens); revoke it there to stop reports from a machine you cannot reach.

### Live
Who reported in the last 15 minutes, tokens and cost so far today, tokens in the last hour and an intraday curve, refreshed every minute. Live rows are kept 14 days.

### Forecast
Month to date, projected month (same-weekday means over the last four weeks, with a likely range), next 30 days at the 7-day pace, trend versus the previous 30 days, and a **calibrated month** once billing reconciliation has seven overlapping days. Formulas are shown on the tiles; the burn-up chart plots the month's cumulative spend against the projection.

### Tables
Per developer (tokens, requests, sessions, estimated cost, share, month to date, projection), per model and per day. Models the catalog cannot price show as **unpriced**, never as zero — set a price on the [prices](/help#prices) card.`,
      "pt-BR": `### A CLI opt-in
Para ver uso e custo estimado **por desenvolvedor**, cada desenvolvedor roda o \`atlas-agent\` na própria máquina (binários de arquivo único para Windows, Linux e macOS acompanham toda release). Ele lê os logs de uso que as ferramentas de código já guardam localmente — Claude Code (\`~/.claude/projects\`) e Codex CLI (\`~/.codex/sessions\`) — agrega por dia e modelo e envia **só contagens**: tokens, requisições e sessões. Nunca prompts, código, caminhos ou nomes de projeto.

\`\`\`bash
atlas-agent --server {origin} --token <ATLAS_TOKEN> --days 7 --dry-run   # prévia, não envia nada
atlas-agent --server {origin} --token <ATLAS_TOKEN> --days 7             # envia (token analyst)
atlas-agent ... --anonymous                                               # pseudônimo estável por máquina em vez do nome
atlas-agent install --server {origin} --token <ATLAS_TOKEN> --every 12h  # agenda no agendador do SO
atlas-agent status | uninstall | watch
\`\`\`

- Rodar de novo é idempotente: a linha de um dia é substituída, nunca contada duas vezes.
- \`install\` salva as configurações em \`~/.atlas-agent\` e registra um agendamento **por usuário** (Agendador de Tarefas, launchd ou cron). Não há serviço nem processo residente: o agendador inicia o agente, ele reporta, ele sai. O servidor não consegue configurá-lo.
- \`--every 5m\` mantém o painel Ao vivo atual; \`watch\` mantém um terminal aberto e reenvia o dia sempre que os transcripts mudam.
- Cursor, Copilot e Gemini CLI não guardam contagens de tokens localmente, então o agente diz isso em vez de adivinhar; eles chegam por [telemetria](/help#telemetry) ou pela [fonte de custo](/help#spend) do Cursor.
- Cada desenvolvedor precisa de um [token de API](/help#tokens) **analyst**; revogue-o lá para parar os relatórios de uma máquina que você não alcança.

### Ao vivo
Quem reportou nos últimos 15 minutos, tokens e custo até agora hoje, tokens na última hora e uma curva intradiária, atualizada a cada minuto. As linhas ao vivo são mantidas por 14 dias.

### Projeção
Mês até agora, mês projetado (médias do mesmo dia da semana nas últimas quatro semanas, com faixa provável), próximos 30 dias no ritmo de 7 dias, tendência contra os 30 dias anteriores e um **mês calibrado** quando a reconciliação com a fatura tem sete dias sobrepostos. As fórmulas aparecem nos indicadores; o gráfico de burn-up traça o gasto acumulado do mês contra a projeção.

### Tabelas
Por desenvolvedor (tokens, requisições, sessões, custo estimado, participação, mês até agora, projeção), por modelo e por dia. Modelos que o catálogo não consegue precificar aparecem como **sem preço**, nunca como zero — defina um preço no card de [preços](/help#prices).`,
    },
  },
  {
    id: "prices",
    group: "aiEstate",
    title: { en: "Model prices", "pt-BR": "Preços dos modelos" },
    body: {
      en: `Estimated cost = tokens × **USD list price per million tokens** (input, output, cache read, cache write) from a versioned builtin catalog (\`ai-prices.json\`, currently **{priceCatalog}**) **plus the prices you set** on the *Model prices* card, per exact model id or regex family (\`^gpt-4o.*\`). Tenant prices win over the builtin ones.

- Models nothing prices are listed as **unpriced** with the tokens seen ({unpriced} right now) and offered as one-click suggestions; they are never counted as zero.
- Saving or removing a price **reprices every stored report** for this tenant.
- A price above 10 000 USD per million tokens is rejected as a unit mistake.
- \`Atlas:AiEstate:PriceCatalogPath\` replaces the builtin file wholesale (same schema: \`version\`, \`models[]\` with a regex \`pattern\` and \`input\`/\`output\`/optional \`cacheRead\`/\`cacheWrite\`).
- Estimates are compared with what providers actually billed under [Spend & cost sources](/help#spend); the ratio calibrates forecasts but never rewrites the estimate itself.`,
      "pt-BR": `Custo estimado = tokens × **preço de lista em USD por milhão de tokens** (entrada, saída, leitura e escrita de cache) de um catálogo embutido versionado (\`ai-prices.json\`, atualmente **{priceCatalog}**) **mais os preços que você define** no card *Preços dos modelos*, por id exato ou família em regex (\`^gpt-4o.*\`). Preços do tenant vencem os embutidos.

- Modelos sem preço em nenhum lugar aparecem como **sem preço** com os tokens vistos ({unpriced} agora) e são oferecidos como sugestões de um clique; nunca são contados como zero.
- Salvar ou remover um preço **reprecifica todo relatório armazenado** deste tenant.
- Um preço acima de 10 000 USD por milhão de tokens é rejeitado como erro de unidade.
- \`Atlas:AiEstate:PriceCatalogPath\` substitui o arquivo embutido por inteiro (mesmo schema: \`version\`, \`models[]\` com \`pattern\` em regex e \`input\`/\`output\`/opcionais \`cacheRead\`/\`cacheWrite\`).
- As estimativas são comparadas com o que os provedores de fato faturaram em [Gasto e fontes de custo](/help#spend); a razão calibra as projeções mas nunca reescreve a estimativa em si.`,
    },
  },
  {
    id: "telemetry",
    group: "aiEstate",
    title: { en: "Telemetry: Atlas as an OpenTelemetry receiver", "pt-BR": "Telemetria: o Atlas como receptor OpenTelemetry" },
    body: {
      en: `The push path: \`POST /api/ai-estate/otlp/v1/metrics\` and \`/v1/traces\` accept **OTLP/HTTP JSON** (protobuf is answered with 415 and a hint). Usage arrives within one export interval, so Live, budgets and forecasts cover every AI in the company, not only what a CLI reads from disk.

Recognised signals:
- Claude Code metrics (\`claude_code.token.usage\`, \`claude_code.cost.usage\`) and Gemini CLI (\`gemini_cli.token.usage\`) — both export OpenTelemetry natively with a handful of environment variables, copy-paste ready on the *Connect telemetry* card;
- the GenAI semantic conventions: \`gen_ai.client.token.usage\` histograms and \`gen_ai.*\` spans, as emitted by gateways such as LiteLLM or any instrumented application, directly or through a collector (\`otlphttp\` exporter with \`encoding: json\`).

\`\`\`bash
export CLAUDE_CODE_ENABLE_TELEMETRY=1
export OTEL_METRICS_EXPORTER=otlp
export OTEL_EXPORTER_OTLP_PROTOCOL=http/json
export OTEL_EXPORTER_OTLP_ENDPOINT={origin}/api/ai-estate/otlp
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <ATLAS_TOKEN>"
\`\`\`

- Authenticated with an **analyst** API token.
- Cumulative counters are turned into deltas per stream; a counter reset is detected and never double-counted.
- **Privacy**: people are pseudonymised by default (\`Atlas:AiEstate:Telemetry:ActorMode=pseudonym\` hashes \`user.email\`/\`user.id\` with the installation key); \`label\` keeps the attribute — decide this with whoever owns privacy. Services appear as \`svc:<service.name>\`.
- The provider is taken from \`gen_ai.provider.name\` or guessed from the model id; unknown models show as unpriced.`,
      "pt-BR": `O caminho de push: \`POST /api/ai-estate/otlp/v1/metrics\` e \`/v1/traces\` aceitam **OTLP/HTTP JSON** (protobuf recebe 415 com uma dica). O uso chega em um intervalo de exportação, então Ao vivo, orçamentos e projeções cobrem toda IA da empresa, não só o que uma CLI lê do disco.

Sinais reconhecidos:
- métricas do Claude Code (\`claude_code.token.usage\`, \`claude_code.cost.usage\`) e do Gemini CLI (\`gemini_cli.token.usage\`) — ambos exportam OpenTelemetry nativamente com poucas variáveis de ambiente, prontas para copiar no card *Conectar telemetria*;
- as convenções semânticas GenAI: histogramas \`gen_ai.client.token.usage\` e spans \`gen_ai.*\`, emitidos por gateways como o LiteLLM ou qualquer aplicação instrumentada, direto ou via collector (exporter \`otlphttp\` com \`encoding: json\`).

\`\`\`bash
export CLAUDE_CODE_ENABLE_TELEMETRY=1
export OTEL_METRICS_EXPORTER=otlp
export OTEL_EXPORTER_OTLP_PROTOCOL=http/json
export OTEL_EXPORTER_OTLP_ENDPOINT={origin}/api/ai-estate/otlp
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <ATLAS_TOKEN>"
\`\`\`

- Autenticado com um token de API **analyst**.
- Contadores cumulativos viram deltas por stream; um reset de contador é detectado e nunca contado duas vezes.
- **Privacidade**: pessoas são pseudonimizadas por padrão (\`Atlas:AiEstate:Telemetry:ActorMode=pseudonym\` faz hash de \`user.email\`/\`user.id\` com a chave da instalação); \`label\` mantém o atributo — decida isso com quem responde pela privacidade. Serviços aparecem como \`svc:<service.name>\`.
- O provedor vem de \`gen_ai.provider.name\` ou é inferido do id do modelo; modelos desconhecidos aparecem sem preço.`,
    },
  },
  {
    id: "spend",
    group: "aiEstate",
    routes: ["/ai-estate?tab=spend"],
    title: { en: "Spend & cost sources: billing, reconciliation, provider guides", "pt-BR": "Gasto e fontes de custo: fatura, reconciliação, guias por provedor" },
    body: {
      en: `### Cost sources (read-only, optional)
Store a provider **admin / billing key** under [Credentials](/help#credentials), then connect it as a cost source on this tab and sync. The key never travels through the AI Estate endpoints. Providers available on this instance: **{costProviders}**.

| Provider | Key | What Atlas reads |
|---|---|---|
| OpenAI | organization admin key | Costs API per project and line item |
| Anthropic | Admin API key | cost report per workspace and description; Claude Code analytics folded to daily aggregates, never per developer |
| GitHub Copilot | PAT with billing read, scope = organization login | billing seats: idle 60+ days, pending cancellation; seat holders as keyed pseudonyms; list-price estimate |
| Cursor | Admin API key (Teams / Enterprise) | members, this cycle's spend per member, per-request token usage (lands in Developer usage as tool \`cursor\`) |

The **How to connect** guide under the form walks through where each key is created and which type is accepted.

### Daily pull
Billing is pulled automatically for every connected source: **{syncEnabled}**, at **{syncHour}:00 UTC**, next run **{syncNext}** (\`Atlas:AiEstate:CostSync:{Enabled,HourUtc,Days}\`). *Sync now* pulls on demand; a failing provider never hides the others, and the last error per source is shown on its card.

### Reconciliation: estimated versus billed
Per provider and per day, the catalog **estimate** (from CLI reports, telemetry and Cursor usage) is compared with what the billing API **reported** for the same days. Once **seven days overlap**, the ratio is usable and calibrates the forecasts (a *calibrated month* tile appears in Developer usage). Provider-reported and estimated amounts are always shown separately and never summed.

### Seats
Copilot and Cursor seat facts (active, idle, pending cancellation) appear here and in the portfolio report — as counts and pseudonyms, never as a list of names.`,
      "pt-BR": `### Fontes de custo (somente leitura, opcionais)
Guarde uma **chave admin / de faturamento** do provedor em [Credenciais](/help#credentials), depois conecte-a como fonte de custo nesta aba e sincronize. A chave nunca passa pelos endpoints do AI Estate. Provedores disponíveis nesta instância: **{costProviders}**.

| Provedor | Chave | O que o Atlas lê |
|---|---|---|
| OpenAI | chave admin da organização | Costs API por projeto e item |
| Anthropic | chave Admin API | relatório de custo por workspace e descrição; analytics do Claude Code agregados por dia, nunca por desenvolvedor |
| GitHub Copilot | PAT com leitura de billing, escopo = login da organização | assentos de billing: ociosos há 60+ dias, cancelamento pendente; titulares como pseudônimos com chave; estimativa a preço de lista |
| Cursor | chave Admin API (Teams / Enterprise) | membros, gasto do ciclo por membro, uso de tokens por requisição (cai em Uso por desenvolvedor como ferramenta \`cursor\`) |

O guia **Como conectar** abaixo do formulário mostra onde cada chave é criada e qual tipo é aceito.

### Puxada diária
A fatura é puxada automaticamente para toda fonte conectada: **{syncEnabled}**, às **{syncHour}:00 UTC**, próxima execução **{syncNext}** (\`Atlas:AiEstate:CostSync:{Enabled,HourUtc,Days}\`). *Sincronizar agora* puxa sob demanda; um provedor com falha nunca esconde os outros, e o último erro por fonte aparece no card dela.

### Reconciliação: estimado versus faturado
Por provedor e por dia, a **estimativa** do catálogo (de relatórios da CLI, telemetria e uso do Cursor) é comparada com o que a API de faturamento **reportou** para os mesmos dias. Quando **sete dias se sobrepõem**, a razão passa a ser usável e calibra as projeções (um indicador de *mês calibrado* aparece em Uso por desenvolvedor). Valores reportados pelo provedor e estimados são sempre mostrados separados e nunca somados.

### Assentos
Fatos de assentos do Copilot e do Cursor (ativos, ociosos, cancelamento pendente) aparecem aqui e no relatório de portfólio — como contagens e pseudônimos, nunca como lista de nomes.`,
    },
  },

  // ───────────────────────────── Settings ─────────────────────────────
  {
    id: "credentials",
    group: "settings",
    routes: ["/credentials"],
    title: { en: "Credentials", "pt-BR": "Credenciais" },
    body: {
      en: `Named secrets for private sources and cost sources: git tokens, GitHub / Azure DevOps / GitLab PATs, provider admin keys.

- **Write-only**: a secret is stored encrypted (AES-256-GCM under \`ATLAS_MASTER_KEY\`) and never returned by the API or the UI; only its name, description and dates are visible. Type a new value to replace it.
- Pick a credential by name when creating an assessment or connecting a cost source.
- Git receives credentials through the environment, never on the command line or in a URL.
- Listing and editing credentials requires the **admin** role when sign-in is on.
- **Back up \`ATLAS_MASTER_KEY\`**: losing it makes every stored credential and the AI provider key unrecoverable; you would re-enter them.`,
      "pt-BR": `Segredos nomeados para fontes privadas e fontes de custo: tokens git, PATs do GitHub / Azure DevOps / GitLab, chaves admin de provedores.

- **Somente escrita**: o segredo é armazenado cifrado (AES-256-GCM sob \`ATLAS_MASTER_KEY\`) e nunca é devolvido pela API nem pela UI; só nome, descrição e datas ficam visíveis. Digite um novo valor para substituir.
- Escolha uma credencial pelo nome ao criar uma avaliação ou conectar uma fonte de custo.
- O git recebe as credenciais pelo ambiente, nunca na linha de comando nem numa URL.
- Listar e editar credenciais exige o papel **admin** quando o login está ativo.
- **Faça backup da \`ATLAS_MASTER_KEY\`**: perdê-la torna toda credencial armazenada e a chave do provedor de IA irrecuperáveis; você teria que reinseri-las.`,
    },
  },
  {
    id: "tokens",
    group: "settings",
    routes: ["/settings/tokens"],
    title: { en: "API tokens and roles", "pt-BR": "Tokens de API e papéis" },
    body: {
      en: `API tokens (\`atlas_pat_…\`) are machine credentials for CI pipelines, scripts and the \`atlas-agent\` CLI. Created under **Settings → API tokens** by an administrator: tenant-bound, role \`analyst\` or \`admin\`, optional expiry, **shown once**, stored hashed. Revoke to stop them instantly; rotate by creating a new one and revoking the old.

Send it as \`Authorization: Bearer atlas_pat_…\`.

### Roles
| Who | Can |
|---|---|
| viewer (any signed-in user) | read everything except the administrative inventories (tokens, credentials, tenants) |
| analyst | viewer + create and run assessments, triage, policies per assessment, report own usage (\`usage/report\`, OTLP) |
| admin | everything: credentials, cost sources, allowlist, prices, budgets, teams, tenant-wide policies, tokens, tenants, deletion |

Roles come from the sign-in provider's claim (\`Atlas:Auth:RoleClaim\`, default role names \`atlas-analyst\` / \`atlas-admin\`) or from the token. With sign-in **off**, every caller is an administrator.`,
      "pt-BR": `Tokens de API (\`atlas_pat_…\`) são credenciais de máquina para pipelines de CI, scripts e a CLI \`atlas-agent\`. Criados em **Configurações → Tokens de API** por um administrador: presos ao tenant, papel \`analyst\` ou \`admin\`, validade opcional, **mostrados uma vez**, armazenados como hash. Revogue para pará-los na hora; rotacione criando um novo e revogando o antigo.

Envie como \`Authorization: Bearer atlas_pat_…\`.

### Papéis
| Quem | Pode |
|---|---|
| viewer (qualquer usuário logado) | ler tudo exceto os inventários administrativos (tokens, credenciais, tenants) |
| analyst | viewer + criar e executar avaliações, triagem, políticas por avaliação, reportar o próprio uso (\`usage/report\`, OTLP) |
| admin | tudo: credenciais, fontes de custo, lista aprovada, preços, orçamentos, times, políticas do tenant, tokens, tenants, exclusão |

Os papéis vêm da claim do provedor de login (\`Atlas:Auth:RoleClaim\`, nomes padrão \`atlas-analyst\` / \`atlas-admin\`) ou do token. Com o login **desligado**, todo chamador é administrador.`,
    },
  },
  {
    id: "ai-settings",
    group: "settings",
    routes: ["/settings/ai"],
    title: { en: "AI provider settings", "pt-BR": "Configurações do provedor de IA" },
    body: {
      en: `Administrators select the provider for the [AI features](/help#ai-assist): **Anthropic**, **OpenAI**, **Azure OpenAI** or a local **Ollama**; set model and base URL; store the key (write-only, encrypted); **Test connection**; and flip the **enable** switch. Until the switch is on, nothing is ever sent to any provider.

- \`Methods per analysis\` bounds token spend for business-rule analysis.
- \`docker compose --profile ai-local up -d\` adds an Ollama container with a small code model for a key-less, offline setup (\`ATLAS_LOCAL_MODEL\`, \`ATLAS_LOCAL_MODEL_MEMORY\`).
- The page shows token usage and the **feedback summary** (👍/👎 per feature and per model) so a provider's cost can be weighed against what people found useful, plus a cost estimate for the estate.
- Secrets findings are never sent to a provider, whatever the settings.`,
      "pt-BR": `Administradores escolhem o provedor dos [recursos de IA](/help#ai-assist): **Anthropic**, **OpenAI**, **Azure OpenAI** ou um **Ollama** local; definem modelo e URL base; guardam a chave (somente escrita, cifrada); **Testam a conexão**; e ligam a chave de **ativação**. Até ela estar ligada, nada é enviado a provedor algum.

- \`Métodos por análise\` limita o gasto de tokens na análise de regras de negócio.
- \`docker compose --profile ai-local up -d\` adiciona um container Ollama com um modelo de código pequeno para uma configuração offline e sem chave (\`ATLAS_LOCAL_MODEL\`, \`ATLAS_LOCAL_MODEL_MEMORY\`).
- A página mostra o uso de tokens e o **resumo de feedback** (👍/👎 por recurso e por modelo) para pesar o custo de um provedor contra o que as pessoas achararam útil, além de uma estimativa de custo para o parque.
- Findings de segredos nunca são enviados a um provedor, sejam quais forem as configurações.`,
    },
  },
  {
    id: "cost-settings",
    group: "settings",
    routes: ["/settings/cost"],
    title: { en: "Cost model", "pt-BR": "Modelo de custo" },
    body: {
      en: `The tenant's **currency**, **hourly rate** and **team size** used by every modernization estimate and report. Deployment defaults come from \`Atlas:Cost:*\` (per-KLOC rates by strategy, hours per blocker and per finding, multipliers, productive hours per developer-month); this page overrides the three market parameters per tenant and *Reset* returns to the defaults.

There is deliberately no currency conversion: a US team is estimated at US rates, a Brazilian team at BRL rates — you state your market's numbers once. Record real outcomes on an assessment's Modernization tab and check *Calibration* on the portfolio to see whether the rates should move.`,
      "pt-BR": `A **moeda**, o **valor-hora** e o **tamanho do time** do tenant usados por toda estimativa de modernização e relatório. Os padrões do deployment vêm de \`Atlas:Cost:*\` (taxas por KLOC por estratégia, horas por bloqueador e por finding, multiplicadores, horas produtivas por desenvolvedor-mês); esta página sobrescreve os três parâmetros de mercado por tenant e *Redefinir* volta aos padrões.

Não há conversão de moeda de propósito: um time americano é estimado a taxas americanas, um time brasileiro a taxas em BRL — você informa os números do seu mercado uma vez. Registre os resultados reais na aba Modernização de uma avaliação e veja *Calibração* no portfólio para saber se as taxas devem mudar.`,
    },
  },
  {
    id: "admin",
    group: "settings",
    routes: ["/settings/admin"],
    title: { en: "Administration: tenants and notification channels", "pt-BR": "Administração: tenants e canais de notificação" },
    body: {
      en: `### Tenants
Each tenant has isolated assessments, findings, settings, channels and governance data (every row carries a tenant id enforced by a global query filter). Register a tenant with a name and an optional **external key** — the value of the sign-in token claim (\`Atlas:Tenants:Claim\`, default \`tid\`) that maps users to it. Unmapped users go to the default tenant or get 403 (\`Atlas:Tenants:AllowUnmappedUsers\`).

### Notification channels (this tenant)
Override the deployment-wide channels: a signed **webhook** (URL, secret, public base URL for links), **Slack** and **Teams Workflows** webhooks, and the **weekly digest** day and hour (UTC). Used for completed runs, the portfolio digest and [budget alerts](/help#budgets). Messages carry scores, counts and links, never finding contents.

Deployment defaults: \`Atlas:Notifications:{WebhookUrl,Secret,PublicBaseUrl,SlackWebhookUrl,TeamsWebhookUrl,DigestDayOfWeek,DigestHourUtc}\`.`,
      "pt-BR": `### Tenants
Cada tenant tem avaliações, findings, configurações, canais e dados de governança isolados (toda linha carrega um id de tenant aplicado por um filtro global de consulta). Registre um tenant com nome e uma **chave externa** opcional — o valor da claim do token de login (\`Atlas:Tenants:Claim\`, padrão \`tid\`) que mapeia usuários para ele. Usuários sem mapeamento vão para o tenant padrão ou recebem 403 (\`Atlas:Tenants:AllowUnmappedUsers\`).

### Canais de notificação (este tenant)
Sobrescreva os canais do deployment: um **webhook** assinado (URL, segredo, URL base pública para links), webhooks de **Slack** e **Teams Workflows**, e o dia e a hora (UTC) do **resumo semanal**. Usados para execuções concluídas, o resumo do portfólio e os [alertas de orçamento](/help#budgets). As mensagens levam scores, contagens e links, nunca conteúdo de findings.

Padrões do deployment: \`Atlas:Notifications:{WebhookUrl,Secret,PublicBaseUrl,SlackWebhookUrl,TeamsWebhookUrl,DigestDayOfWeek,DigestHourUtc}\`.`,
    },
  },
  {
    id: "auth",
    group: "settings",
    title: { en: "Sign-in, roles and tenants (OIDC)", "pt-BR": "Login, papéis e tenants (OIDC)" },
    body: {
      en: `Sign-in is optional and **off by default**; on this instance it is **{auth}**. Any shared instance should turn it on.

- Authorization code + PKCE in the SPA, JWT bearer on the API — Entra ID, Keycloak, Auth0 and any OIDC issuer.
- Environment: \`ATLAS_AUTH_ENABLED=true\`, \`ATLAS_AUTH_AUTHORITY\` (issuer), \`ATLAS_AUTH_CLIENT_ID\` (the SPA client), \`ATLAS_AUTH_AUDIENCE\`. Roles from \`Atlas:Auth:RoleClaim\` with names \`Atlas:Auth:{AdminRole,AnalystRole}\`.
- **Roles**: viewer, analyst, admin — see [API tokens and roles](/help#tokens). Your roles right now: **{roles}**.
- **Tenants**: the claim \`Atlas:Tenants:Claim\` maps a user to a registered tenant ([Administration](/help#admin)). You are in tenant **{tenant}**.
- **Per-assessment access lists** (Viewer / Editor / Owner) restrict individual assessments; hidden ones are 404 for everyone else.
- API tokens work with or without OIDC, so pipelines and the CLI never need an interactive login.`,
      "pt-BR": `O login é opcional e **desligado por padrão**; nesta instância está **{auth}**. Qualquer instância compartilhada deve ligá-lo.

- Authorization code + PKCE na SPA, JWT bearer na API — Entra ID, Keycloak, Auth0 e qualquer emissor OIDC.
- Ambiente: \`ATLAS_AUTH_ENABLED=true\`, \`ATLAS_AUTH_AUTHORITY\` (emissor), \`ATLAS_AUTH_CLIENT_ID\` (o client da SPA), \`ATLAS_AUTH_AUDIENCE\`. Papéis pela \`Atlas:Auth:RoleClaim\` com nomes \`Atlas:Auth:{AdminRole,AnalystRole}\`.
- **Papéis**: viewer, analyst, admin — veja [Tokens de API e papéis](/help#tokens). Seus papéis agora: **{roles}**.
- **Tenants**: a claim \`Atlas:Tenants:Claim\` mapeia um usuário a um tenant registrado ([Administração](/help#admin)). Você está no tenant **{tenant}**.
- **Listas de acesso por avaliação** (Leitor / Editor / Dono) restringem avaliações individuais; as ocultas são 404 para os demais.
- Tokens de API funcionam com ou sem OIDC, então pipelines e a CLI nunca precisam de login interativo.`,
    },
  },

  // ───────────────────────────── Integrate ─────────────────────────────
  {
    id: "ci",
    group: "integrate",
    title: { en: "CI integration, quality gate and PR comment", "pt-BR": "Integração com CI, quality gate e comentário de PR" },
    body: {
      en: `\`deploy/ci/atlas-ci.sh\` (bash + curl + jq) and \`atlas-ci.ps1\` (PowerShell) find or create the assessment for the current repository, queue a run, wait, download SARIF and evaluate the **gate**:

\`\`\`
GET /api/assessments/{id}/gate?failOn=High&minScore=60&failOnNew=High
\`\`\`

- \`failOn\`: fail when an open finding of this severity or above exists.
- \`minScore\`: fail below this health score.
- \`failOnNew\`: **baseline mode** for legacy estates — only findings the latest run *introduced or reintroduced* fail the gate; the existing stock of debt never blocks a pipeline, regressions do. The first completed run establishes the baseline.

Ready-made wrappers: the composite **GitHub Action** \`.github/actions/atlas-scan\`, the **Azure DevOps** template \`deploy/ci/azure-pipelines-atlas.yml\` and the **GitLab CI** template \`deploy/ci/gitlab-atlas.yml\`. Pipelines authenticate with an **API token** (\`atlas_pat_…\`).

### Pull-request comment
\`GET /api/assessments/{id}/pr-comment?failOn=&minScore=&failOnNew=&lang=&ai=\` renders Markdown: gate verdict, health with its delta, open findings by severity, what the run changed, the new findings to review, the gate's reasons and links back to Atlas. The Action posts and updates its own comment; \`ai=true\` adds a two-sentence note when a provider is configured.

### Bringing other tools in
POST a SARIF 2.1.0 log to \`/api/assessments/{id}/sarif\` (or upload it on the Settings tab) and ESLint, Semgrep, Trivy or CodeQL findings become first-class, triageable, scored findings.`,
      "pt-BR": `\`deploy/ci/atlas-ci.sh\` (bash + curl + jq) e \`atlas-ci.ps1\` (PowerShell) encontram ou criam a avaliação do repositório atual, enfileiram uma execução, esperam, baixam o SARIF e avaliam o **gate**:

\`\`\`
GET /api/assessments/{id}/gate?failOn=High&minScore=60&failOnNew=High
\`\`\`

- \`failOn\`: falha quando existe um finding aberto desta severidade ou acima.
- \`minScore\`: falha abaixo deste health score.
- \`failOnNew\`: **modo baseline** para parques legados — só findings que a última execução *introduziu ou reintroduziu* falham o gate; o estoque existente de dívida nunca bloqueia um pipeline, regressões sim. A primeira execução concluída estabelece a baseline.

Wrappers prontos: a **GitHub Action** composta \`.github/actions/atlas-scan\`, o template do **Azure DevOps** \`deploy/ci/azure-pipelines-atlas.yml\` e o template do **GitLab CI** \`deploy/ci/gitlab-atlas.yml\`. Os pipelines se autenticam com um **token de API** (\`atlas_pat_…\`).

### Comentário de pull request
\`GET /api/assessments/{id}/pr-comment?failOn=&minScore=&failOnNew=&lang=&ai=\` renderiza Markdown: veredito do gate, saúde com o delta, findings abertos por severidade, o que a execução mudou, os findings novos a revisar, os motivos do gate e links de volta ao Atlas. A Action publica e atualiza o próprio comentário; \`ai=true\` adiciona uma nota de duas frases quando há provedor configurado.

### Trazendo outras ferramentas
Faça POST de um log SARIF 2.1.0 em \`/api/assessments/{id}/sarif\` (ou envie na aba Configurações) e findings do ESLint, Semgrep, Trivy ou CodeQL viram findings de primeira classe, triáveis e pontuados.`,
    },
  },
  {
    id: "api",
    group: "integrate",
    title: { en: "REST API basics", "pt-BR": "Fundamentos da API REST" },
    body: {
      en: `Everything the UI does goes through \`/api\`, so anything you see can be scripted. Base address on this instance: \`{origin}/api\`.

- **Authentication**: \`Authorization: Bearer atlas_pat_…\` ([API tokens](/help#tokens)) or the OIDC JWT the SPA uses. With sign-in off, no header is needed.
- **Rate limit**: per IP on \`/api\` (\`Atlas:Operations:RateLimitPerMinute\`).
- **Long operations** return \`202\` with a job id; follow it on \`GET /api/jobs\` or the events stream \`GET /api/events/jobs\`.
- **Language**: \`?lang=en|pt-BR\` on reports and rule catalog.

\`\`\`bash
# create and follow an assessment
curl -s -X POST {origin}/api/assessments -H 'Content-Type: application/json' \\
  -d '{"name":"Billing platform","sourceKind":"local","sourceLocator":"/sources/billing"}'
curl -s {origin}/api/assessments/<id>/health
curl -s "{origin}/api/assessments/<id>/report?lang=en" -o report.html
# portfolio, rules, AI estate, usage
curl -s {origin}/api/portfolio
curl -s {origin}/api/rules
curl -s {origin}/api/ai-estate
curl -s "{origin}/api/ai-estate/usage?days=30"
curl -s "{origin}/api/ai-estate/usage/export.csv?days=30" -o usage.csv
\`\`\`

Key groups: \`/api/assessments\` (lifecycle, findings, triage, gate, reports, exports, SARIF import, sharing), \`/api/portfolio\`, \`/api/rules\`, \`/api/jobs\`, \`/api/credentials\`, \`/api/tokens\`, \`/api/tenants\`, \`/api/settings/{cost,notifications}\`, \`/api/ai/settings\`, \`/api/ai-estate/*\` (inventory, allowlist, cost sources, usage, live, prices, reconciliation, budgets, teams, OTLP), \`/api/version\`, \`/metrics\`, \`/health/{live,ready}\`.`,
      "pt-BR": `Tudo que a UI faz passa por \`/api\`, então qualquer coisa que você vê pode ser automatizada. Endereço base nesta instância: \`{origin}/api\`.

- **Autenticação**: \`Authorization: Bearer atlas_pat_…\` ([tokens de API](/help#tokens)) ou o JWT OIDC que a SPA usa. Com login desligado, nenhum header é necessário.
- **Rate limit**: por IP em \`/api\` (\`Atlas:Operations:RateLimitPerMinute\`).
- **Operações longas** respondem \`202\` com um id de job; acompanhe em \`GET /api/jobs\` ou no stream de eventos \`GET /api/events/jobs\`.
- **Idioma**: \`?lang=en|pt-BR\` em relatórios e no catálogo de regras.

\`\`\`bash
# criar e acompanhar uma avaliação
curl -s -X POST {origin}/api/assessments -H 'Content-Type: application/json' \\
  -d '{"name":"Billing platform","sourceKind":"local","sourceLocator":"/sources/billing"}'
curl -s {origin}/api/assessments/<id>/health
curl -s "{origin}/api/assessments/<id>/report?lang=pt-BR" -o report.html
# portfólio, regras, AI estate, uso
curl -s {origin}/api/portfolio
curl -s {origin}/api/rules
curl -s {origin}/api/ai-estate
curl -s "{origin}/api/ai-estate/usage?days=30"
curl -s "{origin}/api/ai-estate/usage/export.csv?days=30" -o usage.csv
\`\`\`

Grupos principais: \`/api/assessments\` (ciclo de vida, findings, triagem, gate, relatórios, exportações, import SARIF, compartilhamento), \`/api/portfolio\`, \`/api/rules\`, \`/api/jobs\`, \`/api/credentials\`, \`/api/tokens\`, \`/api/tenants\`, \`/api/settings/{cost,notifications}\`, \`/api/ai/settings\`, \`/api/ai-estate/*\` (inventário, lista aprovada, fontes de custo, uso, ao vivo, preços, reconciliação, orçamentos, times, OTLP), \`/api/version\`, \`/metrics\`, \`/health/{live,ready}\`.`,
    },
  },

  // ───────────────────────────── Operate ─────────────────────────────
  {
    id: "configuration",
    group: "operate",
    title: { en: "Configuration reference", "pt-BR": "Referência de configuração" },
    body: {
      en: `### Environment (\`.env\`, read by docker-compose)
| Variable | Purpose |
|---|---|
| \`ATLAS_DB_PASSWORD\`, \`ATLAS_MINIO_PASSWORD\` | data service passwords |
| \`ATLAS_SECRETS_HMAC_KEY\` | base64, exactly 32 bytes — keyed fingerprints of secrets and pseudonyms |
| \`ATLAS_MASTER_KEY\` | base64, exactly 32 bytes — encryption of stored credentials and AI keys. **Back it up.** The API and worker refuse to start on a malformed key and print how to generate one |
| \`ATLAS_LOCAL_SOURCES\`, \`_2\`, \`_3\` | host folders mounted read-only as \`/sources\`, \`/sources-2\`, \`/sources-3\` |
| \`ATLAS_TIER2_ENABLED\` | \`true\` runs \`dotnet restore\` in the sandbox (SDK worker image) |
| \`ATLAS_LOCAL_MODEL\`, \`ATLAS_LOCAL_MODEL_MEMORY\` | Ollama model and memory for the \`ai-local\` profile |
| \`ATLAS_AUTH_ENABLED\`, \`_AUTHORITY\`, \`_CLIENT_ID\`, \`_AUDIENCE\` | optional OIDC |
| \`ATLAS_VERSION\` | image tag when running from published images (\`docker-compose.images.yml\`) |

### Application settings (\`Atlas__Section__Key\` in compose)
| Section | Keys |
|---|---|
| \`Atlas:Scanning\` | \`Isolation\` (ChildProcess / InProcess), \`ChildMemoryLimitMb\`, \`ChildTimeoutMinutes\`, \`ScannerTimeoutMinutes\`, \`MaxFiles\`, \`Tier2:{Enabled,PackageCache}\` |
| \`Atlas:Connectors:Git\` | \`AllowedHosts\`, \`AllowFileUrls\`, \`HistoryMonths\` |
| \`Atlas:Connectors:{GitHub,AzureDevOps,GitLab}\` | base URLs for self-hosted servers |
| \`Atlas:Vulnerabilities\` | \`SyncEnabled\`, \`SyncUrls\` (add OSV \`Maven/all.zip\` and \`PyPI/all.zip\` for Java/Python CVEs), \`OsvBundlePath\` |
| \`Atlas:Licenses\` | \`Enabled\`, \`CachePath\`, \`Denied\`, \`MaxLookupsPerRun\`, \`Concurrency\` |
| \`Atlas:Cost\` | cost model parameters; currency, hourly rate and team size overridable per tenant |
| \`Atlas:Report\` | \`BrandName\`, \`PreparedBy\`, \`LogoDataUri\`, \`AccentColor\`, \`PdfServiceUrl\`, \`ChromiumPath\` |
| \`Atlas:Notifications\` | \`WebhookUrl\`, \`Secret\`, \`PublicBaseUrl\`, \`SlackWebhookUrl\`, \`TeamsWebhookUrl\`, \`DigestDayOfWeek\`, \`DigestHourUtc\` — overridable per tenant |
| \`Atlas:Uploads\` | \`MaxArchiveBytes\`, \`MaxExtractedBytes\`, \`MaxEntries\`, \`OrphanRetentionHours\` |
| \`Atlas:Auth\` | \`Enabled\`, \`Authority\`, \`ClientId\`, \`Audience\`, \`RoleClaim\`, \`AdminRole\`, \`AnalystRole\` |
| \`Atlas:Tenants\` | \`Claim\`, \`AllowUnmappedUsers\` |
| \`Atlas:Operations\` | \`JsonLogs\`, \`MetricsEnabled\`, \`AuditEnabled\`, \`RateLimitPerMinute\` |
| \`Atlas:Ai\` | \`LocalOllamaUrl\`, \`LocalModel\` |
| \`Atlas:AiEstate\` | \`ApprovedProviders\` (fallback allowlist), \`CatalogPath\`, \`PriceCatalogPath\`, \`Telemetry:ActorMode\` (pseudonym / label), \`CostSync:{Enabled,HourUtc,Days}\` |
| \`Atlas:AutoMigrate\` | \`true\` applies EF Core migrations (both schemas) when the API starts — single-node deployments |

Settings apply to **both** the API and the worker unless noted; restart the containers after changing them.`,
      "pt-BR": `### Ambiente (\`.env\`, lido pelo docker-compose)
| Variável | Finalidade |
|---|---|
| \`ATLAS_DB_PASSWORD\`, \`ATLAS_MINIO_PASSWORD\` | senhas dos serviços de dados |
| \`ATLAS_SECRETS_HMAC_KEY\` | base64, exatamente 32 bytes — fingerprints com chave de segredos e pseudônimos |
| \`ATLAS_MASTER_KEY\` | base64, exatamente 32 bytes — cifra das credenciais armazenadas e das chaves de IA. **Faça backup.** API e worker recusam iniciar com chave malformada e imprimem como gerar uma |
| \`ATLAS_LOCAL_SOURCES\`, \`_2\`, \`_3\` | pastas do host montadas somente leitura como \`/sources\`, \`/sources-2\`, \`/sources-3\` |
| \`ATLAS_TIER2_ENABLED\` | \`true\` roda \`dotnet restore\` no sandbox (imagem SDK do worker) |
| \`ATLAS_LOCAL_MODEL\`, \`ATLAS_LOCAL_MODEL_MEMORY\` | modelo e memória do Ollama para o profile \`ai-local\` |
| \`ATLAS_AUTH_ENABLED\`, \`_AUTHORITY\`, \`_CLIENT_ID\`, \`_AUDIENCE\` | OIDC opcional |
| \`ATLAS_VERSION\` | tag da imagem ao rodar a partir das imagens publicadas (\`docker-compose.images.yml\`) |

### Configurações da aplicação (\`Atlas__Secao__Chave\` no compose)
| Seção | Chaves |
|---|---|
| \`Atlas:Scanning\` | \`Isolation\` (ChildProcess / InProcess), \`ChildMemoryLimitMb\`, \`ChildTimeoutMinutes\`, \`ScannerTimeoutMinutes\`, \`MaxFiles\`, \`Tier2:{Enabled,PackageCache}\` |
| \`Atlas:Connectors:Git\` | \`AllowedHosts\`, \`AllowFileUrls\`, \`HistoryMonths\` |
| \`Atlas:Connectors:{GitHub,AzureDevOps,GitLab}\` | URLs base de servidores self-hosted |
| \`Atlas:Vulnerabilities\` | \`SyncEnabled\`, \`SyncUrls\` (adicione \`Maven/all.zip\` e \`PyPI/all.zip\` do OSV para CVEs de Java/Python), \`OsvBundlePath\` |
| \`Atlas:Licenses\` | \`Enabled\`, \`CachePath\`, \`Denied\`, \`MaxLookupsPerRun\`, \`Concurrency\` |
| \`Atlas:Cost\` | parâmetros do modelo de custo; moeda, valor-hora e tamanho do time sobrescrevíveis por tenant |
| \`Atlas:Report\` | \`BrandName\`, \`PreparedBy\`, \`LogoDataUri\`, \`AccentColor\`, \`PdfServiceUrl\`, \`ChromiumPath\` |
| \`Atlas:Notifications\` | \`WebhookUrl\`, \`Secret\`, \`PublicBaseUrl\`, \`SlackWebhookUrl\`, \`TeamsWebhookUrl\`, \`DigestDayOfWeek\`, \`DigestHourUtc\` — sobrescrevíveis por tenant |
| \`Atlas:Uploads\` | \`MaxArchiveBytes\`, \`MaxExtractedBytes\`, \`MaxEntries\`, \`OrphanRetentionHours\` |
| \`Atlas:Auth\` | \`Enabled\`, \`Authority\`, \`ClientId\`, \`Audience\`, \`RoleClaim\`, \`AdminRole\`, \`AnalystRole\` |
| \`Atlas:Tenants\` | \`Claim\`, \`AllowUnmappedUsers\` |
| \`Atlas:Operations\` | \`JsonLogs\`, \`MetricsEnabled\`, \`AuditEnabled\`, \`RateLimitPerMinute\` |
| \`Atlas:Ai\` | \`LocalOllamaUrl\`, \`LocalModel\` |
| \`Atlas:AiEstate\` | \`ApprovedProviders\` (lista aprovada de fallback), \`CatalogPath\`, \`PriceCatalogPath\`, \`Telemetry:ActorMode\` (pseudonym / label), \`CostSync:{Enabled,HourUtc,Days}\` |
| \`Atlas:AutoMigrate\` | \`true\` aplica as migrations do EF Core (os dois schemas) quando a API sobe — deployments de um nó |

As configurações valem para **ambos** API e worker salvo indicação; reinicie os containers depois de alterá-las.`,
    },
  },
  {
    id: "operations",
    group: "operate",
    title: { en: "Operations: install, upgrade, backup, monitoring", "pt-BR": "Operação: instalação, upgrade, backup, monitoramento" },
    body: {
      en: `### Install
\`docker compose up -d --build\` from a checkout, or \`docker compose -f docker-compose.yml -f docker-compose.images.yml up -d\` with the published images (\`ghcr.io/fsqbr/atlas-{api,worker,web}:<version>\`). Fill \`.env\` first (passwords, the two 32-byte keys, source roots). The API applies migrations on start when \`Atlas__AutoMigrate=true\`; the worker waits for the schema before consuming jobs.

### Upgrade
Pull the new version, rebuild or bump \`ATLAS_VERSION\`, \`docker compose up -d\`. Migrations are additive and idempotent. Take a backup first for a major jump. If the UI looks unchanged afterwards, press *Ctrl+F5* once.

### Backup and restore
\`deploy/scripts/backup.sh|.ps1\` dumps the database (both schemas), uploads, MinIO and \`.env\`; \`deploy/scripts/restore.sh <folder>\` restores and restarts. The OSV bundle and clone workspaces are caches. **The dump is useless without the same \`ATLAS_MASTER_KEY\`.**

### Monitoring
- \`GET /metrics\` (Prometheus): jobs by state, assessments, average health, open findings by severity, HTTP metrics.
- \`GET /health/live\` and \`/health/ready\` (dependencies reachable).
- \`Atlas:Operations:JsonLogs=true\` for one JSON object per line. A first boot on an empty database logs no errors; any \`Error\` at startup is worth reading.

### Smoke checks
\`python deploy/scripts/smoke-check.py {origin}\` runs about 57 end-to-end checks against a live instance (it writes data: run it on a fresh install or staging). \`npm run e2e\` in \`src/Atlas.Web\` drives a browser through every page.`,
      "pt-BR": `### Instalação
\`docker compose up -d --build\` a partir de um checkout, ou \`docker compose -f docker-compose.yml -f docker-compose.images.yml up -d\` com as imagens publicadas (\`ghcr.io/fsqbr/atlas-{api,worker,web}:<versão>\`). Preencha o \`.env\` antes (senhas, as duas chaves de 32 bytes, raízes de fontes). A API aplica as migrations ao subir quando \`Atlas__AutoMigrate=true\`; o worker espera o schema antes de consumir jobs.

### Upgrade
Puxe a nova versão, reconstrua ou atualize \`ATLAS_VERSION\`, \`docker compose up -d\`. As migrations são aditivas e idempotentes. Faça backup antes de um salto grande. Se a UI parecer igual depois, pressione *Ctrl+F5* uma vez.

### Backup e restauração
\`deploy/scripts/backup.sh|.ps1\` exporta o banco (os dois schemas), uploads, MinIO e \`.env\`; \`deploy/scripts/restore.sh <pasta>\` restaura e reinicia. O bundle do OSV e os workspaces de clone são caches. **O dump é inútil sem a mesma \`ATLAS_MASTER_KEY\`.**

### Monitoramento
- \`GET /metrics\` (Prometheus): jobs por estado, avaliações, saúde média, findings abertos por severidade, métricas HTTP.
- \`GET /health/live\` e \`/health/ready\` (dependências alcançáveis).
- \`Atlas:Operations:JsonLogs=true\` para um objeto JSON por linha. Um primeiro boot em banco vazio não registra erros; qualquer \`Error\` na subida merece leitura.

### Verificações de fumaça
\`python deploy/scripts/smoke-check.py {origin}\` roda cerca de 57 verificações de ponta a ponta contra uma instância viva (ele grava dados: rode numa instalação nova ou em staging). \`npm run e2e\` em \`src/Atlas.Web\` conduz um navegador por todas as páginas.`,
    },
  },
  {
    id: "security",
    group: "operate",
    title: { en: "Security and privacy guarantees", "pt-BR": "Garantias de segurança e privacidade" },
    body: {
      en: `- **No execution of analyzed code.** Parsers and readers only; Tier 2 (\`dotnet restore\`) is opt-in and runs in the disposable scan-host process.
- **Egress**: only the API talks to the internet — OSV sync, license metadata, AI providers when enabled, provider billing APIs when connected. The worker has no internet access.
- **Isolation**: child process per run with heap and time limits; per-scanner timeouts; file caps; data services on an internal network; read-only root filesystems and dropped capabilities in compose.
- **Secrets**: credentials and AI keys are AES-256-GCM encrypted and never returned; secrets *findings* are fingerprinted, never stored in clear, and never sent to an AI provider.
- **People**: developer usage is counts only; telemetry identities are pseudonymised by default; Copilot and Cursor seat holders are keyed pseudonyms; Claude Code analytics are folded to daily aggregates. Team attribution in reports is by tags.
- **Tenants**: a global query filter on every row; the tenant comes from a token claim.
- **Audit and limits**: every state-changing call is recorded; per-IP rate limiting; webhooks signed with HMAC-SHA256 and carrying scores and counts only.
- **AI output never changes scores or findings** and is always labelled.

Report vulnerabilities as described in the repository's SECURITY.md.`,
      "pt-BR": `- **Nenhuma execução do código analisado.** Só parsers e leitores; o Tier 2 (\`dotnet restore\`) é opt-in e roda no processo descartável de scan.
- **Saída de rede**: só a API fala com a internet — sync do OSV, metadados de licenças, provedores de IA quando ativados, APIs de faturamento quando conectadas. O worker não tem acesso à internet.
- **Isolamento**: processo filho por execução com limites de heap e tempo; timeouts por scanner; limites de arquivos; serviços de dados em rede interna; sistemas de arquivos raiz somente leitura e capabilities removidas no compose.
- **Segredos**: credenciais e chaves de IA são cifradas com AES-256-GCM e nunca devolvidas; *findings* de segredos têm fingerprint, nunca são armazenados em claro e nunca vão para um provedor de IA.
- **Pessoas**: o uso por desenvolvedor é só contagem; identidades de telemetria são pseudonimizadas por padrão; titulares de assentos do Copilot e do Cursor são pseudônimos com chave; os analytics do Claude Code são agregados por dia. A atribuição a times nos relatórios é por tags.
- **Tenants**: filtro global de consulta em toda linha; o tenant vem de uma claim do token.
- **Auditoria e limites**: toda chamada que muda estado é registrada; rate limit por IP; webhooks assinados com HMAC-SHA256 levando só scores e contagens.
- **A saída da IA nunca muda scores ou findings** e é sempre rotulada.

Reporte vulnerabilidades conforme o SECURITY.md do repositório.`,
    },
  },
  {
    id: "troubleshooting",
    group: "operate",
    title: { en: "Troubleshooting", "pt-BR": "Solução de problemas" },
    body: {
      en: `| Symptom | Cause and fix |
|---|---|
| "Could not load data from the API" banner | The API is down or not reachable through the web proxy. Check \`docker compose ps\` and \`{origin}/api/version\`. |
| The UI looks unchanged after an upgrade | The browser kept the old shell. Press *Ctrl+F5* once. |
| API or worker exits at start mentioning \`MasterKeyBase64\` | \`ATLAS_MASTER_KEY\` is not base64 or not 32 bytes. Generate with \`openssl rand -base64 32\` and restart. |
| Verdict "nothing to assess" | No analyzable source at the locator: wrong folder, everything excluded by scope, or an unsupported language. |
| Run failed with "Local source directory not found" | The path is outside the mounted roots or the root is not mounted (\`ATLAS_LOCAL_SOURCES\`). |
| Clone refused | The host is not in \`Atlas:Connectors:Git:AllowedHosts\`, or the URL embeds credentials. |
| Jobs stay queued | The worker is not running or is waiting for migrations; check its logs. |
| 403 on an administrative page | Your token or account lacks the admin role; see [roles](/help#tokens). |
| No Java/Python CVEs | Add the OSV \`Maven/all.zip\` / \`PyPI/all.zip\` exports to \`Atlas:Vulnerabilities:SyncUrls\`. |
| Cost source shows an error | The key is wrong or lacks billing scope; the card shows the provider's message. A failing provider never hides the others. |
| Models appear as "unpriced" | Not in the price catalog; set a price on the Model prices card. Never counted as zero. |
| Budget alerts not arriving | No channel configured (Administration or the team), or the delivery error is shown on the alert. |
| Telemetry returns 415 | Sender is using protobuf; set \`OTEL_EXPORTER_OTLP_PROTOCOL=http/json\`. |
| PDF export fails | The Gotenberg sidecar (\`atlas-pdf\`) is not running or \`Atlas:Report:PdfServiceUrl\` is wrong. |`,
      "pt-BR": `| Sintoma | Causa e correção |
|---|---|
| Faixa "Não foi possível carregar dados da API" | A API está fora ou não é alcançável pelo proxy web. Verifique \`docker compose ps\` e \`{origin}/api/version\`. |
| A UI parece igual depois de um upgrade | O navegador guardou a shell antiga. Pressione *Ctrl+F5* uma vez. |
| API ou worker sai na subida mencionando \`MasterKeyBase64\` | \`ATLAS_MASTER_KEY\` não é base64 ou não tem 32 bytes. Gere com \`openssl rand -base64 32\` e reinicie. |
| Veredito "nada a avaliar" | Nenhuma fonte analisável no locator: pasta errada, tudo excluído pelo escopo ou linguagem não suportada. |
| Execução falhou com "Local source directory not found" | O caminho está fora das raízes montadas ou a raiz não está montada (\`ATLAS_LOCAL_SOURCES\`). |
| Clone recusado | O host não está em \`Atlas:Connectors:Git:AllowedHosts\`, ou a URL embute credenciais. |
| Jobs ficam na fila | O worker não está rodando ou está esperando as migrations; veja os logs dele. |
| 403 numa página administrativa | Seu token ou conta não tem o papel admin; veja [papéis](/help#tokens). |
| Sem CVEs de Java/Python | Adicione os exports \`Maven/all.zip\` / \`PyPI/all.zip\` do OSV em \`Atlas:Vulnerabilities:SyncUrls\`. |
| Fonte de custo mostra erro | A chave está errada ou sem escopo de billing; o card mostra a mensagem do provedor. Um provedor com falha nunca esconde os outros. |
| Modelos aparecem como "sem preço" | Não estão no catálogo de preços; defina um preço no card Preços dos modelos. Nunca contados como zero. |
| Alertas de orçamento não chegam | Nenhum canal configurado (Administração ou o time), ou o erro de entrega aparece no alerta. |
| Telemetria responde 415 | O emissor está usando protobuf; defina \`OTEL_EXPORTER_OTLP_PROTOCOL=http/json\`. |
| Exportação de PDF falha | O sidecar Gotenberg (\`atlas-pdf\`) não está rodando ou \`Atlas:Report:PdfServiceUrl\` está errado. |`,
    },
  },
  {
    id: "glossary",
    group: "operate",
    title: { en: "Glossary", "pt-BR": "Glossário" },
    body: {
      en: `- **Assessment** — one system (repository or folder) under analysis, with its runs, findings and settings.
- **Run** — one execution of every scanner over a materialized source; runs are compared by fingerprint.
- **Finding** — one occurrence of a rule at a location, with severity, confidence, status and remediation.
- **Fingerprint** — stable identity of a finding across runs (rule, location, normalized content; never severity).
- **Health score** — 0–100 from five weighted dimensions; every lost point attributed to a rule.
- **Waiver** — a suppression or false-positive decision, with reason, author and optional expiry.
- **Policy** — a standing waiver by rule and path pattern, per assessment or tenant-wide.
- **Gate** — the pass/fail evaluation a pipeline asks for (\`failOn\`, \`minScore\`, \`failOnNew\`).
- **Baseline mode** — gating only on findings the latest run introduced or reintroduced.
- **AI Estate** — the inventory of AI providers, frameworks, models and MCP servers a repository uses.
- **MCP** — Model Context Protocol; servers coding assistants call for tools and data.
- **Cost source** — a read-only connection to a provider's billing API through a stored credential.
- **Usage fact** — one day, actor, tool and model with token counts and estimated cost.
- **Actor** — a developer (name or pseudonym) or a service (\`svc:<name>\`) that used AI.
- **Reconciliation** — the comparison of estimated cost with billed cost for the same provider and days.
- **Unpriced** — a model with tokens but no catalog or tenant price; shown, never counted as zero.
- **Tenant** — an isolated space of assessments, settings and governance data.`,
      "pt-BR": `- **Avaliação** — um sistema (repositório ou pasta) em análise, com suas execuções, findings e configurações.
- **Execução** — uma passagem de todos os scanners sobre uma fonte materializada; execuções são comparadas por fingerprint.
- **Finding** — uma ocorrência de uma regra num local, com severidade, confiança, status e remediação.
- **Fingerprint** — identidade estável de um finding entre execuções (regra, local, conteúdo normalizado; nunca severidade).
- **Health score** — 0–100 a partir de cinco dimensões ponderadas; cada ponto perdido atribuído a uma regra.
- **Waiver** — uma decisão de supressão ou falso positivo, com motivo, autor e validade opcional.
- **Política** — um waiver permanente por regra e padrão de caminho, por avaliação ou para o tenant.
- **Gate** — a avaliação passa/falha que um pipeline pede (\`failOn\`, \`minScore\`, \`failOnNew\`).
- **Modo baseline** — gate só sobre findings que a última execução introduziu ou reintroduziu.
- **AI Estate** — o inventário de provedores, frameworks, modelos e servidores MCP de IA que um repositório usa.
- **MCP** — Model Context Protocol; servidores que assistentes de código chamam para ferramentas e dados.
- **Fonte de custo** — uma conexão somente leitura à API de faturamento de um provedor via credencial armazenada.
- **Fato de uso** — um dia, ator, ferramenta e modelo com contagens de tokens e custo estimado.
- **Ator** — um desenvolvedor (nome ou pseudônimo) ou um serviço (\`svc:<nome>\`) que usou IA.
- **Reconciliação** — a comparação do custo estimado com o custo faturado para o mesmo provedor e dias.
- **Sem preço** — um modelo com tokens mas sem preço no catálogo ou no tenant; mostrado, nunca contado como zero.
- **Tenant** — um espaço isolado de avaliações, configurações e dados de governança.`,
    },
  },
];

/** Section id for a route (path + optional ?tab=), or null when no section claims it. */
export function helpSectionForRoute(pathname: string, search = ""): string | null {
  const tab = new URLSearchParams(search).get("tab");
  const withTab = tab ? `${pathname}?tab=${tab}` : null;
  for (const section of HELP_SECTIONS) {
    if (withTab && section.routes?.includes(withTab)) return section.id;
  }
  for (const section of HELP_SECTIONS) {
    if (section.routes?.includes(pathname)) return section.id;
  }
  if (/^\/assessments\/[^/]+$/.test(pathname)) {
    const byTab: Record<string, string> = {
      findings: "findings",
      ai: "ai-estate",
      waivers: "waivers",
      runs: "runs",
      modernization: "modernization",
      rules: "ai-assist",
      report: "report",
      settings: "assessment-settings",
    };
    return (tab && byTab[tab]) || "assessment-overview";
  }
  return null;
}
