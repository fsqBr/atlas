# Changelog

Release notes for Atlas. Versions follow `MAJOR.MINOR.PATCH`; container images are published to
`ghcr.io/fsqbr/atlas-{api,worker,web}` with the same tag.

## v0.36.0 — first public release

Everything below ships in this release; later entries will list what changed.

### Assessing
- Sources: mounted local folders with a folder picker, browser upload of a folder on your machine, git URLs, and
  GitHub / Azure DevOps / GitLab locators with repository discovery and stored (encrypted) credentials.
- Analysis without executing code: C# and VB.NET through Roslyn, SQL and JavaScript/TypeScript through text parsers,
  project files, lockfiles, configuration, Dockerfiles and compose files. Opt-in Tier 2 (`dotnet restore` in a sandbox).
- Scanners: dependencies (NuGet + npm against OSV, end-of-life frameworks, weighted migration blockers), security,
  secrets, personal data and leakage, quality (complexity, duplication, tests, coverage reports, legacy APIs),
  architecture (cycles, fan-out, change hotspots and knowledge silos from git history), database footprint,
  infrastructure, front-end frameworks, license compliance with a CycloneDX SBOM.
- Findings with stable fingerprints across runs, triage (suppress, false positive, standing policies per rule/path),
  scope exclusions and `.atlasignore`, aggregation, a rule-regression corpus as a test gate.

### Understanding
- Health score (0–100, five weighted dimensions, every lost point attributed to a rule).
- Six modernization strategies ranked on evidence, effort/duration/cost ranges with explicit assumptions, a phased
  roadmap, calibration against real outcomes.
- Executive report (HTML and PDF, English and Portuguese, white-label), CSV/JSON/SARIF exports, SBOM, run-to-run and
  side-by-side comparisons, portfolio view with benchmark quartiles and targets.

### AI (bring your own key — Anthropic, OpenAI, Azure OpenAI or local Ollama)
- Explain a finding, suggest a fix as a unified diff, recover business rules from decision-heavy methods, write the
  executive summary, draft a migration plan, add a reviewer note to the pull-request comment. Always labelled, cached,
  never a source of scores or findings; thumbs up/down feedback with a perceived-quality view.

### Operating
- Web UI: portfolio dashboard, live assessment overview, charts across the product, light/dark theme, presentation
  mode, EN/PT.
- CI: quality gate scripts (bash, PowerShell), composite GitHub Action and Azure DevOps template, SARIF upload and a
  pull-request comment that updates itself.
- Platform: multi-tenant with per-assessment access lists, optional OIDC sign-in and RBAC, API tokens, scheduled runs
  with signed webhooks, Prometheus metrics, JSON logs, audit trail, rate limiting, scan isolation in a disposable
  child process, Docker Compose and a Helm chart, backup/restore scripts.
