using Atlas.Domain.Modernization;

namespace Atlas.Application.Assessments;

/// <summary>
/// Human text for the modernization engines' keys, EN and PT-BR. Engines emit
/// keys; presentation renders them here — one place to review the wording.
/// </summary>
public static class ModernizationTexts
{
    public static bool IsPt(string? lang) => lang is not null && lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

    public static string Strategy(ModernizationStrategy strategy, string? lang) => (strategy, IsPt(lang)) switch
    {
        (ModernizationStrategy.KeepStabilize, false) => "Keep & stabilize",
        (ModernizationStrategy.KeepStabilize, true) => "Manter e estabilizar",
        (ModernizationStrategy.UpgradeInPlace, false) => "Upgrade in place",
        (ModernizationStrategy.UpgradeInPlace, true) => "Upgrade no lugar",
        (ModernizationStrategy.Incremental, false) => "Incremental modernization",
        (ModernizationStrategy.Incremental, true) => "Modernização incremental",
        (ModernizationStrategy.Strangler, false) => "Strangler pattern",
        (ModernizationStrategy.Strangler, true) => "Strangler (estrangulamento gradual)",
        (ModernizationStrategy.PartialRewrite, false) => "Partial rewrite",
        (ModernizationStrategy.PartialRewrite, true) => "Reescrita parcial",
        (ModernizationStrategy.FullRewrite, false) => "Full rewrite",
        (ModernizationStrategy.FullRewrite, true) => "Reescrita completa",
        _ => strategy.ToString(),
    };

    public static string StrategyDescription(ModernizationStrategy strategy, string? lang) => (strategy, IsPt(lang)) switch
    {
        (ModernizationStrategy.KeepStabilize, false) => "No platform change: fix security debt, update dependencies, keep the current runtime.",
        (ModernizationStrategy.KeepStabilize, true) => "Sem mudança de plataforma: corrigir dívida de segurança, atualizar dependências, manter o runtime atual.",
        (ModernizationStrategy.UpgradeInPlace, false) => "Move every project to modern .NET keeping the architecture; blockers are replaced one by one.",
        (ModernizationStrategy.UpgradeInPlace, true) => "Levar todos os projetos ao .NET moderno mantendo a arquitetura; bloqueadores substituídos um a um.",
        (ModernizationStrategy.Incremental, false) => "Migrate project by project behind .NET Standard bridges, shipping continuously.",
        (ModernizationStrategy.Incremental, true) => "Migrar projeto a projeto atrás de pontes .NET Standard, entregando continuamente.",
        (ModernizationStrategy.Strangler, false) => "Put a façade in front of the legacy system and replace it slice by slice with new services.",
        (ModernizationStrategy.Strangler, true) => "Colocar uma fachada na frente do legado e substituí-lo fatia a fatia por serviços novos.",
        (ModernizationStrategy.PartialRewrite, false) => "Rewrite the bounded contexts that carry the hard blockers; upgrade the rest in place.",
        (ModernizationStrategy.PartialRewrite, true) => "Reescrever os contextos que carregam os bloqueadores duros; fazer upgrade do restante no lugar.",
        (ModernizationStrategy.FullRewrite, false) => "Rebuild the system on a new architecture and run both in parallel until cut-over.",
        (ModernizationStrategy.FullRewrite, true) => "Reconstruir o sistema em nova arquitetura e operar os dois em paralelo até o corte.",
        _ => string.Empty,
    };

    public static string Text(string key, string? lang)
    {
        var pt = IsPt(lang);
        return key switch
        {
            // rationale
            "rationale.no-legacy-frameworks" => pt ? "Nenhum projeto em framework legado" : "No project on a legacy framework",
            "rationale.legacy-frameworks-present" => pt ? "Há projetos em .NET Framework (fora de suporte ou saindo)" : "Projects still on .NET Framework (out of or leaving support)",
            "rationale.security-debt" => pt ? "Dívida de segurança crítica/alta em aberto" : "Open critical/high security debt",
            "rationale.no-hard-blockers" => pt ? "Sem bloqueadores duros (System.Web, WCF, Remoting, WF, MSMQ)" : "No hard blockers (System.Web, WCF, Remoting, WF, MSMQ)",
            "rationale.hard-blockers-present" => pt ? "Bloqueadores duros presentes exigem redesenho de partes do sistema" : "Hard blockers present require redesigning parts of the system",
            "rationale.ui-no-upgrade-path" => pt ? "Projetos em WebForms/MVC 5/Web API 2/WCF/Silverlight não têm caminho de upgrade — essa camada precisa ser reescrita" : "WebForms/MVC 5/Web API 2/WCF/Silverlight projects have no upgrade path — that layer must be rewritten",
            "rationale.desktop-upgrade-path" => pt ? "WinForms/WPF têm caminho suportado para .NET moderno (Windows)" : "WinForms/WPF have a supported path onto modern .NET (Windows)",
            "rationale.web-ui-rewrite" => pt ? "UI ASP.NET clássica (WebForms/MVC 5) não tem caminho de upgrade direto" : "Classic ASP.NET UI (WebForms/MVC 5) has no direct upgrade path",
            "rationale.small-estate" => pt ? "Estate pequeno" : "Small estate",
            "rationale.medium-estate" => pt ? "Estate de porte médio, bom para fatiar" : "Medium-sized estate, good to slice",
            "rationale.large-estate" => pt ? "Estate grande" : "Large estate",
            "rationale.test-deficit" => pt ? "Déficit de testes aumenta o risco de qualquer mudança" : "Test deficit raises the risk of any change",
            "rationale.tests-enable-refactoring" => pt ? "Testes existentes dão rede de segurança para refatorar" : "Existing tests give a safety net for refactoring",
            "rationale.coupling" => pt ? "Ciclos de dependência pedem desacoplamento antes de migrar" : "Dependency cycles call for decoupling before migrating",
            "rationale.edge-replaceable" => pt ? "Bordas (UI/integrações) substituíveis por serviços novos atrás de uma fachada" : "Edges (UI/integrations) replaceable by new services behind a façade",
            "rationale.blockers-concentrated" => pt ? "Bloqueadores concentrados em poucos projetos" : "Blockers concentrated in a few projects",
            "rationale.blockers-spread" => pt ? "Bloqueadores espalhados por grande parte dos projetos" : "Blockers spread across most projects",
            "rationale.small-estate-many-blockers" => pt ? "Estate pequeno com bloqueadores duros: reescrever pode custar menos que migrar" : "Small estate with hard blockers: rewriting may cost less than migrating",
            "rationale.rewrite-without-tests" => pt ? "Reescrever sem testes é o cenário de maior risco" : "Rewriting without tests is the highest-risk scenario",

            // prerequisites
            "prereq.security-remediation" => pt ? "Corrigir findings críticos/altos de segurança" : "Fix critical/high security findings",
            "prereq.dependency-updates" => pt ? "Atualizar pacotes vulneráveis" : "Update vulnerable packages",
            "prereq.sdk-style" => pt ? "Converter csproj para SDK-style" : "Convert csproj to SDK-style",
            "prereq.package-reference" => pt ? "Migrar packages.config para PackageReference" : "Migrate packages.config to PackageReference",
            "prereq.characterization-tests" => pt ? "Testes de caracterização nos fluxos críticos" : "Characterization tests on critical flows",
            "prereq.netstandard-bridge" => pt ? "Bibliotecas compartilhadas em .NET Standard 2.0" : "Shared libraries on .NET Standard 2.0",
            "prereq.facade-routing" => pt ? "Fachada/gateway com roteamento por rota ou recurso" : "Façade/gateway routing by route or resource",
            "prereq.observability" => pt ? "Observabilidade comparável entre legado e novo" : "Comparable observability across legacy and new",
            "prereq.boundary-definition" => pt ? "Definir os limites do que será reescrito" : "Define the boundary of what gets rewritten",
            "prereq.business-rule-inventory" => pt ? "Inventário de regras de negócio" : "Business rule inventory",
            "prereq.parallel-run-plan" => pt ? "Plano de operação paralela e corte" : "Parallel-run and cut-over plan",

            // blockers
            "blocker.eol-runtime" => pt ? "Runtime fora de suporte continua" : "Out-of-support runtime remains",
            "blocker.mb-003" => pt ? "System.Web / WebForms" : "System.Web / WebForms",
            "blocker.mb-007" => "WCF",
            "blocker.mb-008" => ".NET Remoting",
            "blocker.mb-009" => "Workflow Foundation",
            "blocker.mb-010" => "MSMQ",
            "blocker.integration-protocols" => pt ? "Protocolos de integração legados (WCF/Remoting/MSMQ)" : "Legacy integration protocols (WCF/Remoting/MSMQ)",
            "blocker.size" => pt ? "Tamanho do estate torna a reescrita completa impraticável" : "Estate size makes a full rewrite impractical",

            // benefits
            "benefit.lowest-cost" => pt ? "Menor custo" : "Lowest cost",
            "benefit.no-functional-change" => pt ? "Sem mudança funcional" : "No functional change",
            "benefit.supported-runtime" => pt ? "Runtime suportado (segurança e performance)" : "Supported runtime (security and performance)",
            "benefit.performance" => pt ? "Ganho de performance" : "Performance gains",
            "benefit.same-architecture" => pt ? "Arquitetura preservada" : "Architecture preserved",
            "benefit.continuous-delivery" => pt ? "Entrega contínua durante a migração" : "Continuous delivery during migration",
            "benefit.risk-spread" => pt ? "Risco distribuído em fatias" : "Risk spread across slices",
            "benefit.parallel-run" => pt ? "Legado e novo coexistem" : "Legacy and new coexist",
            "benefit.new-architecture" => pt ? "Nova arquitetura" : "New architecture",
            "benefit.remove-hard-blockers" => pt ? "Elimina os bloqueadores duros" : "Removes hard blockers",
            "benefit.keep-stable-core" => pt ? "Núcleo estável preservado" : "Stable core preserved",

            // effort breakdown
            "effort.base" => pt ? "Esforço base (por KLOC)" : "Base effort (per KLOC)",
            "effort.rewrite-share" => pt ? "Parte reescrita (KLOC)" : "Rewritten share (KLOC)",
            "effort.upgrade-share" => pt ? "Parte com upgrade (KLOC)" : "Upgraded share (KLOC)",
            "effort.blockers-prerequisite" => pt ? "Bloqueadores pré-requisito" : "Prerequisite blockers",
            "effort.blockers-high" => pt ? "Bloqueadores de impacto alto" : "High-impact blockers",
            "effort.blockers-medium" => pt ? "Bloqueadores de impacto médio" : "Medium-impact blockers",
            "effort.security-critical" => pt ? "Segurança — críticos" : "Security — critical",
            "effort.security-high" => pt ? "Segurança — altos" : "Security — high",
            "effort.security-medium" => pt ? "Segurança — médios" : "Security — medium",
            "effort.secrets" => pt ? "Segredos a rotacionar" : "Secrets to rotate",
            "effort.vulnerable-packages" => pt ? "Pacotes vulneráveis" : "Vulnerable packages",

            // assumptions
            "assumption.lines-of-code" => pt ? "Linhas de código analisadas" : "Lines of code analyzed",
            "assumption.hours-per-kloc" => pt ? "Horas por KLOC (estratégia)" : "Hours per KLOC (strategy)",
            "assumption.no-tests" => pt ? "Sem testes automatizados — multiplicador" : "No automated tests — multiplier",
            "assumption.low-coverage" => pt ? "Cobertura baixa — multiplicador aplicado" : "Low coverage — multiplier applied",
            "assumption.unknown-coverage" => pt ? "Cobertura desconhecida — multiplicador" : "Coverage unknown — multiplier",
            "assumption.high-complexity" => pt ? "Complexidade média alta (multiplicador)" : "High average complexity (multiplier)",
            "assumption.coupling" => pt ? "Ciclos de dependência (multiplicador)" : "Dependency cycles (multiplier)",
            "assumption.team-size" => pt ? "Tamanho do time" : "Team size",
            "assumption.hours-per-month" => pt ? "Horas produtivas por dev/mês" : "Productive hours per developer-month",
            "assumption.hourly-rate" => pt ? "Valor-hora" : "Hourly rate",
            "assumption.range-factors" => pt ? "Fatores otimista / conservador" : "Optimistic / conservative factors",
            "assumption.confidence" => pt ? "Confiança da estimativa" : "Estimate confidence",

            // calibration
            "calibration.none" => pt ? "Nenhum resultado real registrado ainda." : "No real outcome recorded yet.",
            "calibration.too-few" => pt ? "Poucos pontos para calibrar (mínimo 3); registre mais projetos concluídos." : "Too few points to calibrate (3 needed); record more finished projects.",
            "calibration.raise-rates" => pt ? "O modelo subestima: realizado acima do provável em mais de 25% — aumente as taxas por KLOC em Atlas:Cost." : "The model under-estimates: actuals run more than 25% above likely — raise the per-KLOC rates in Atlas:Cost.",
            "calibration.lower-rates" => pt ? "O modelo superestima: realizado abaixo de 80% do provável — reduza as taxas por KLOC em Atlas:Cost." : "The model over-estimates: actuals below 80% of likely — lower the per-KLOC rates in Atlas:Cost.",
            "calibration.ok" => pt ? "Estimativas dentro de ±25% do realizado; mantenha os parâmetros." : "Estimates within ±25% of actuals; keep the parameters.",

            // phases
            "phase.baseline" => pt ? "Fase 0 — Baseline" : "Phase 0 — Baseline",
            "phase.security" => pt ? "Fase 1 — Estabilização de segurança" : "Phase 1 — Security stabilization",
            "phase.tests" => pt ? "Fase 2 — Testes de caracterização" : "Phase 2 — Characterization tests",
            "phase.foundation" => pt ? "Fase 3 — Fundação (SDK-style, TFM, pacotes)" : "Phase 3 — Foundation (SDK-style, TFM, packages)",
            "phase.domain" => pt ? "Fase 4 — Migração do domínio" : "Phase 4 — Domain migration",
            "phase.data-integration" => pt ? "Fase 5 — Dados e integrações" : "Phase 5 — Data and integrations",
            "phase.retirement" => pt ? "Fase 6 — Aposentadoria do legado" : "Phase 6 — Legacy retirement",

            // work items
            "work.inventory" => pt ? "Inventário e mapa de dependências dos projetos" : "Inventory and dependency map of projects",
            "work.health-baseline" => pt ? "Baseline do índice de saúde e métricas" : "Health score and metrics baseline",
            "work.fix-critical" => pt ? "Corrigir findings críticos de segurança" : "Fix critical security findings",
            "work.fix-high" => pt ? "Corrigir findings altos de segurança" : "Fix high security findings",
            "work.rotate-secrets" => pt ? "Rotacionar e remover segredos do código" : "Rotate and remove secrets from code",
            "work.update-vulnerable-packages" => pt ? "Atualizar pacotes com vulnerabilidades conhecidas" : "Update packages with known vulnerabilities",
            "work.characterization-tests" => pt ? "Testes de caracterização por projeto sem cobertura" : "Characterization tests per uncovered project",
            "work.coverage-pipeline" => pt ? "Medição de cobertura no pipeline" : "Coverage measurement in the pipeline",
            "work.sdk-style" => pt ? "Converter projetos para SDK-style" : "Convert projects to SDK-style",
            "work.package-reference" => pt ? "Migrar packages.config para PackageReference" : "Migrate packages.config to PackageReference",
            "work.target-framework" => pt ? "Retarget de projetos legados" : "Retarget legacy projects",
            "work.medium-blockers" => pt ? "Substituir bloqueadores de impacto médio (MVC 5, Web API 2, EF6…)" : "Replace medium-impact blockers (MVC 5, Web API 2, EF6…)",
            "work.high-blockers" => pt ? "Redesenhar componentes com bloqueadores duros" : "Redesign components with hard blockers",
            "work.web-ui" => pt ? "Reescrever a UI web (Razor Pages/Blazor/SPA)" : "Rewrite the web UI (Razor Pages/Blazor/SPA)",
            "work.migrate-projects" => pt ? "Migrar projetos para .NET moderno" : "Migrate projects to modern .NET",
            "work.strangler-slices" => pt ? "Substituir fatias atrás da fachada" : "Replace slices behind the façade",
            "work.rewrite-bounded-context" => pt ? "Reescrever contextos com bloqueadores" : "Rewrite contexts carrying blockers",
            "work.rewrite-all" => pt ? "Reconstruir o sistema" : "Rebuild the system",
            "work.ef-core" => pt ? "Migrar EF6 para EF Core" : "Migrate EF6 to EF Core",
            "work.integration-protocols" => pt ? "Substituir WCF/Remoting/MSMQ por gRPC/REST/mensageria" : "Replace WCF/Remoting/MSMQ with gRPC/REST/messaging",
            "work.decommission" => pt ? "Desligar o legado após o corte" : "Decommission the legacy system after cut-over",

            _ => key,
        };
    }

    public static string Confidence(EstimateConfidence confidence, string? lang) => (confidence, IsPt(lang)) switch
    {
        (EstimateConfidence.High, true) => "Alta",
        (EstimateConfidence.Medium, true) => "Média",
        (EstimateConfidence.Low, true) => "Baixa",
        _ => confidence.ToString(),
    };
}
