# Changelog

Release notes for Atlas. Versions follow `MAJOR.MINOR.PATCH`; container images are published to
`ghcr.io/fsqbr/atlas-{api,worker,web}` with the same tag.

## v0.40.0

- **Cost model per tenant** (*Settings -> Cost model*, `GET|PUT|DELETE /api/settings/cost`): currency, hourly rate
  and team size are the tenant's market facts — a US estate is estimated at US$ rates, not at converted BRL. Every
  estimate and report of the tenant uses them; frozen calibration records keep the values of their time.
- **Waivers with expiry**: suppressing a finding (and suppression policies) can carry an expiry — "accepted for 90
  days". Expired policies stop filtering on the next run; expired finding waivers are reopened automatically by an
  hourly sweep, and the health score is recomputed.
- **Portfolio groups (tags)**: free-form labels per assessment (*Settings -> Tags*, `PUT /api/assessments/{id}/tags`),
  with a group roll-up (count, average score, open findings) and click-to-filter on the portfolio page.
- **Weekly digest**: `Atlas:Notifications:DigestDayOfWeek` (+ `DigestHourUtc`) posts a weekly portfolio pulse to the
  Slack/Teams webhooks — average health and open findings vs seven days ago, top movers, goals at risk. Counts only.
- **SARIF import** (`POST /api/assessments/{id}/sarif`, also under *Settings*): bring ESLint/Semgrep/Trivy/CodeQL
  results into an assessment as first-class findings — each tool becomes its own scanner, re-imports resolve what the
  tool no longer reports, and Atlas's own scans never touch them.
- Portfolio trend: young estates (less than two weeks of history) are sampled daily, so the chart lives from day one.
- Migrations: `AddTenantCostProfiles`, `AddSuppressionExpiry`, `AddAssessmentTags`.

## v0.39.0

- **Rule catalog page** (`/rules`, `GET /api/rules`): every rule Atlas checks — scanner, category, description
  (EN/PT), open findings and affected assessments in your estate — with **per-tenant severity tuning**
  (`PUT /api/rules/{id}/severity`, admin). Tuned severities apply to candidates from each assessment's next run;
  fingerprints exclude severity, so finding history is preserved. Silencing a rule entirely remains the job of
  suppression policies (reason + audit trail).
- **Portfolio trend by dimension**: the average-health chart gained a metric selector (Overall, Security,
  Modernization, Dependencies, Architecture, Quality) — `GET /api/portfolio/trend` now returns per-dimension
  weekly averages, recomputed from the persisted health snapshots.
- Migration `AddRuleSeverityOverrides` (table `rule_severity_overrides`).

## v0.38.0

Rule and scoring accuracy release: a deep audit of every scanner rule and evaluation confirmed 35 defects; all are
fixed here. Highlights:

- Health score: a crashed/timed-out scan host no longer collapses the score (the size normalization now falls back
  to the persisted inventory); the report's totals come from the full open set instead of a 5,000-row page; failed
  runs are never used as comparison baselines.
- Secrets: prefixed credential names (`db_password`, `dbPassword`, `aws_secret_access_key`) and unquoted `.env`/YAML
  assignments are now detected; new detectors for Anthropic, OpenAI, GitLab and npm tokens; UTF-16 config files are
  scanned.
- Dependencies/OSV: exact-pin (`[12.0.1]`) and floating (`13.*`) versions are matched instead of silently skipped;
  multi-branch advisories no longer double-count; CVSS vector-only severities are scored (a 9.8 vector was landing
  on Medium); npm aliased packages match by their real name; `Microsoft.AspNet.WebApi.Client` is no longer a
  migration blocker.
- Privacy: `nameof(...)` is no longer treated as leaked data; `dialog`/`catalog` are not log sinks; service/validator
  fields are not PII inventory.
- Quality/architecture: git history follows renames and ignores bot authors (hotspots and knowledge silos survive a
  repository reorganization); production libraries referencing `xunit.abstractions` are not test projects; coverage
  by reference walks the project-reference closure; duplicated coverage reports are ingested once; duplication
  blocks no longer fuse adjacent duplicates of different partners.
- Licenses/infra/SQL/JS: `AND` with an unknown license term now surfaces as unknown; lowercase `or`/`and` SPDX
  operators parse; the EOL base-image catalog covers node 18/20, .NET 9.0, postgres 13, mongo 5/6, alpine 3.19/3.20
  and friends; `FROM <stage>` references are not "unpinned images"; parameterized `sp_executesql` next to unrelated
  arithmetic is no longer "dynamic SQL"; hand-written `jquery.*.js` plugins are scanned.
- Evaluations: targets count the whole target day (UTC); benchmark percentiles use midpoint ranking (a uniform
  estate reads 50, not 100); cost bands keep width on small estates; calibration compares actuals against the
  estimate frozen at record time (new `EstimatedHours` on actuals, migration `AddCalibrationEstimate`); the
  portfolio trend follows triage recomputes; tenant-wide suppression policies apply to existing findings
  immediately; the report's `?since=` returns "no changes" when nothing ran after the baseline.

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
