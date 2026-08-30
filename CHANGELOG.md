# Changelog

Release notes for Atlas. Versions follow `MAJOR.MINOR.PATCH`; container images are published to
`ghcr.io/fsqbr/atlas-{api,worker,web}` with the same tag.

## v0.37.0

- Portfolio history: the portfolio page now charts average health and open findings week by week
  (`GET /api/portfolio/trend?weeks=`), recomputed from the runs that already happened — each assessment counts with
  its latest completed run up to that week.
- Report baseline: the executive report (HTML and PDF) accepts `?since=` and the Report tab gained a "compare with"
  picker — the "what changed" section compares against the chosen older run instead of only the previous one, for a
  monthly executive view.
- Slack and Teams notifications: `Atlas:Notifications:SlackWebhookUrl` (incoming webhook) and `TeamsWebhookUrl`
  (Teams Workflows webhook) receive every completed run as a formatted card — score with delta, new/resolved/regressed
  counts, target status and a link. Counts only, never finding contents.
- GitLab CI template `deploy/ci/gitlab-atlas.yml`: include it remotely, and with a project access token it posts and
  keeps updating the merge-request comment (SARIF and the comment are also kept as artifacts).
- Repository: Dependabot watches NuGet, npm, GitHub Actions and Docker images; release badge in the README.

## v0.36.1

- Run Atlas straight from the published images, no local build: `docker compose -f docker-compose.yml -f docker-compose.images.yml up -d` (pin a release with `ATLAS_VERSION`). Building from source remains the path for Tier 2's SDK worker image.
- Quick start rewritten from a fresh-clone walkthrough: key generation on Windows PowerShell, expected first-build time, bundled legacy sample step by step, Windows/WSL 2 notes and a troubleshooting table.
- Folder picker: the same host folder mounted more than once (the `_2`/`_3` defaults) now shows a single root.

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
