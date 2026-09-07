# Atlas — Runbook (self-hosted)

How to install, upgrade, back up, monitor and troubleshoot an Atlas instance running with Docker Compose.

## 1. Install

Requirements: Docker Engine 24+ with Compose v2, 4 vCPU / 8 GB RAM (the worker child process is capped at 2 GB
heap, the API at 1 GB), 20 GB disk (Postgres + OSV bundle ~75 MB + clone workspaces), outbound HTTPS from the API
(OSV sync) and from the worker (git hosts on the allow-list).

```bash
git clone <repo> atlas && cd atlas
cp .env.example .env
# fill in: ATLAS_DB_PASSWORD, ATLAS_MINIO_PASSWORD, ATLAS_SECRETS_HMAC_KEY, ATLAS_MASTER_KEY
# (each key: openssl rand -base64 32), ATLAS_LOCAL_SOURCES[_2,_3] (host folders mounted read-only as picker roots)
docker compose up -d --build
curl -s http://localhost:3000/health/ready # -> Healthy
```

Published ports: web UI `:3000` (nginx, proxies `/api` to the API), API `:8080`. Postgres, MinIO and the PDF sidecar
live on the internal network only.

Released images: `ghcr.io/<owner>/atlas-{api,worker,web}:<version>` (pushed by the `Release` workflow on `v*` tags);
to run from images instead of building, replace each `build:` block in `docker-compose.yml` with `image:`.

## 2. Configuration you will actually touch

| Setting (env) | Purpose |
| --- | --- |
| `ATLAS_LOCAL_SOURCES`, `_2`, `_3` | Host folders exposed read-only as `/sources`, `/sources-2`, `/sources-3` — the roots of the UI folder picker |
| `Atlas__Uploads__*` | Browser-upload archive directory (`atlas-uploads` volume) and caps: `MaxArchiveBytes` (1 GB), `MaxExtractedBytes` (4 GB), `MaxEntries` (300k), `OrphanRetentionHours` (24; unreferenced archives older than this are deleted hourly) |
| `ATLAS_MASTER_KEY` | AES-256-GCM key for stored git credentials **and the AI provider key** — **back it up**; losing it loses both |

Both keys must be valid base64 of exactly 32 bytes. Since v0.59.0 the API and the worker refuse to start on a malformed
`ATLAS_MASTER_KEY` (the message names the decoded length and the `openssl rand -base64 32` command); earlier versions
started and answered 500 on every credential or AI-settings request instead.
| `ATLAS_TIER2_ENABLED` (`Atlas__Scanning__Tier2__*`) | Opt-in `dotnet restore` in the scan sandbox for project-accurate symbols; requires the SDK worker image (`ATLAS_WORKER_BASE` build arg) and a writable `PackageCache` |
| *Settings → API tokens* (`/api/tokens`, admin) | Service tokens `atlas_pat_…` for CI: tenant-bound, analyst/admin, expiry, revocation; hash-only storage. Rotate by creating a new one and revoking the old |
| `Atlas__Tenants__Claim` / `AllowUnmappedUsers` | Token claim mapped to a tenant's external key (default `tid`); unmapped users → default tenant (true) or 403 (false). Manage tenants with `/api/tenants` (admin) |
| `docker compose --profile ai-local` / `ATLAS_LOCAL_MODEL` | Bundled Ollama (no key, offline). Volume `atlas-ollama-models`; first start downloads the model. Stop with `docker compose --profile ai-local stop atlas-ollama` to free RAM |
| *Settings → AI* (UI, admin) | AI provider, model, base URL, key (write-only) and the enable switch; `Methods per analysis` bounds token spend. Nothing is sent to a provider while the switch is off |
| `ATLAS_SECRETS_HMAC_KEY` | Keyed fingerprints of detected secrets (stable finding identity across restarts) |
| `ATLAS_AUTH_*` | OIDC (issuer, SPA client id, audience). Off by default; required for any shared instance |
| `Atlas__Connectors__Git__AllowedHosts__N` | Egress allow-list for clones |
| `Atlas__Connectors__Git__HistoryMonths` | Commit history window for churn rules (0 = shallow) |
| `Atlas__Cost__*` | Cost model parameters (team size, hourly rate, per-KLOC rates) |
| `Atlas__Report__LogoDataUri`, `Atlas__Report__AccentColor` | White-label PDF |
| `Atlas__Notifications__WebhookUrl`, `__Secret`, `__PublicBaseUrl` | Tenant-wide run.completed webhook |
| `Atlas__Operations__RateLimitPerMinute`, `__JsonLogs`, `__MetricsEnabled`, `__AuditEnabled` | Ops knobs |
| `Atlas__Scanning__Isolation`, `__ChildMemoryLimitMb`, `__ChildTimeoutMinutes`, `__ScannerTimeoutMinutes`, `__MaxFiles` | Worker guard rails |

### AI Estate: allowlist, catalog and cost sources

- Approved providers are edited on the *AI estate* page (`PUT /api/ai-estate/allowlist`, admin) and stored per tenant; every run
  receives them as a scan setting. The variables below are only the fallback when nothing is saved.
- `Atlas__AiEstate__ApprovedProviders__N` (API **and** worker): provider ids the organization approved. Any external
  provider found in a repository and not listed raises `ai.unapproved-provider` (High). Leave unset to keep the rule silent.
- `Atlas__AiEstate__CatalogPath`: a custom `ai-signatures.json` (same schema as the embedded one) replaces the builtin
  catalog; an invalid file logs a warning and falls back to the builtin. The catalog version and hash appear in the report.
- Cost sources are admin operations: store the provider key under `PUT /api/credentials/{name}`, connect it with
  `PUT /api/ai-estate/cost/sources/{openai|anthropic|github-copilot|cursor}` (`credentialName`, `scope` = GitHub org for
  Copilot), pull with `POST /api/ai-estate/cost/sync` or let the daily pull do it: `Atlas__AiEstate__CostSync__Enabled`
  (default true), `__HourUtc` (default 6), `__Days` (default 7). `GET /api/ai-estate/cost/schedule` shows the next and
  last run; results and the last error per provider are in `GET /api/ai-estate/cost/sources`.
- Cost data lives in the `governance` schema with its own migrations history (`governance.__ef_migrations_history`);
  `Atlas__AutoMigrate=true` migrates both schemas. Back it up with the rest of the database.
- Developer usage reports (`atlas-agent` CLI) land in `governance.usage_facts` through `POST /api/ai-estate/usage/report`;
  that one route needs only the analyst role so developers can use a personal API token. Give each developer a token from
  *Settings → API tokens*; revoke it there to stop their reports. `GET /api/ai-estate/usage?days=N` is the summary.
- Model prices can be set in the UI (*AI estate → Developer usage & prices*, admin): per tenant, consulted before the
  builtin catalog, stored in `governance.model_prices`; every change reprices the stored usage reports.
- `atlas-agent install` on a developer machine writes `~/.atlas-agent/config.json` (contains the API token) and a per-user
  scheduled task/launchd agent/cron line that runs `atlas-agent run`; revoke the token in *Settings → API tokens* to stop
  reports from a machine you cannot reach, and tell the developer to run `atlas-agent uninstall`.
- Cursor cost source: an Admin API key (`key_…`, Teams/Enterprise plans) stored under Credentials; no scope. Sync reads
  `/teams/members`, `/teams/spend` and `/teams/filtered-usage-events` (paginated, window-bounded). Members are stored as
  `id-…` pseudonyms (installation HMAC key) — the same scheme as telemetry identities.
- Reconciliation (`GET /api/ai-estate/usage/reconciliation`) needs both sides for the same provider and days: usage
  (CLI, telemetry or Cursor) and a synced cost source. Keep `POST /api/ai-estate/cost/sync` running daily (cron/CI) so
  the seven-day overlap builds up; the blended ratio is then applied to forecasts automatically.
- OpenTelemetry receiver: `POST /api/ai-estate/otlp/v1/{metrics,traces}`, JSON only (senders: `OTEL_EXPORTER_OTLP_PROTOCOL=http/json`;
  collector `otlphttp` exporter: `encoding: json`), authenticated with an analyst API token in the `Authorization` header.
  `Atlas__AiEstate__Telemetry__ActorMode=pseudonym` (default) hashes `user.email`/`user.id` into `otel-…` with the
  installation HMAC key; `label` keeps the attribute — decide this with whoever owns privacy before turning it on.
  Cumulative counters are tracked in `governance.telemetry_streams` (pruned after 3 idle days).
- Budget alerts are evaluated after every ingest and delivered to the tenant's channels (Settings → Administration:
  webhook, Slack, Teams); delivery errors are stored on the alert and visible in the UI. Thresholds are fixed at
  50/80/100 % once per month; the anomaly rule is 3× the 7-day median and at least 5 USD, once per day.
- Live usage keeps one row per report in `governance.usage_report_events` (who, when, the day's running totals — no
  content), pruned automatically after 14 days. Agents installed with `--every 5m` write ~300 rows/day each; that is the
  intended volume.
- `Atlas__AiEstate__PriceCatalogPath`: a custom `ai-prices.json` (same schema as the embedded one: `version`, `models[]`
  with regex `pattern` and USD per million tokens for `input`, `output`, optional `cacheRead`/`cacheWrite`) replaces the
  builtin list prices used for developer-usage estimates; an invalid file logs a warning and falls back to the builtin.
  Models outside the catalog appear as *unpriced* in the UI rather than as zero cost.

### Browser smoke (Playwright)

`cd src/Atlas.Web && BASE_URL=http://localhost:3000 npm run e2e` drives Chromium through every page (both themes), the
AI estate tabs, creating a budget, the API proxy and the cache headers of the shell and assets. CI runs it on every push
against a stack built from the commit. It creates one budget named `e2e budget <timestamp>`; remove it afterwards on a
production instance.

### Smoke check after install or upgrade

`python deploy/scripts/smoke-check.py http://localhost:8080 [token]` runs ~55 checks against a live instance: creates
and runs an assessment of the bundled sample, reads reports and exports, triages, and exercises the whole AI Estate
module (allowlist, prices, CLI report, OTLP ingest, live view, forecast, teams, budgets and alerts, reconciliation,
cost-source validation, API tokens). It prints PASS/FAIL per check and exits non-zero on any failure. It writes data
into the instance (assessment, team, budget, tokens) — run it on a fresh install or a staging copy, not production.

## 3. Upgrade

```bash
git pull # or bump image tags
docker compose up -d --build
```

Schema changes are EF Core migrations applied by the API on start (`Atlas__AutoMigrate=true`). Migrations are
additive and idempotent; the worker keeps running against the old schema until the API has migrated, so start the API
first if you orchestrate manually. Always take a backup (below) before a major upgrade.

## 4. Backup & restore

What holds state: the `atlas-pgdata` volume (everything: assessments, findings, credentials ciphertext, policies,
audit) and the `.env` file (master key). `atlas-uploads` holds browser-uploaded archives (needed for re-runs of "upload" assessments). `atlas-vulndata` (OSV bundle) and `atlas-workspaces` (clones) are caches and
regenerate.

```bash
# backup
docker compose exec -T atlas-postgres pg_dump -U atlas -d atlas -Fc > atlas-$(date +%F).dump
cp .env atlas-$(date +%F).env # keep it in the same secure place as the dump

# restore (empty instance)
docker compose up -d atlas-postgres
docker compose exec -T atlas-postgres pg_restore -U atlas -d atlas --clean --if-exists < atlas-YYYY-MM-DD.dump
docker compose up -d
```

## 5. Monitoring

- `GET /health/live`, `GET /health/ready` (Postgres reachable).
- `GET /metrics` (Prometheus): `atlas_scan_jobs{state}`, `atlas_assessments`, `atlas_health_score_average`,
  `atlas_open_findings{severity}`, `atlas_audit_entries_total`, plus ASP.NET HTTP request metrics.
- `GET /api/vulnerabilities/status`: OSV bundle snapshot and last sync result.
- Queue page (`/jobs`): dead-letter jobs (3 failed attempts) can be retried there.
- Logs: `docker compose logs -f atlas-api atlas-worker`; set `Atlas__Operations__JsonLogs=true` for one JSON object
  per line (ship with any log collector).
- Audit trail: `GET /api/audit?take=200` — every mutating API call with actor, method, path, status.

## 6. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Run stays `Running` after a worker restart | The job lease (30 min) must expire before another worker claims it; wait, or expire it in SQL in dev |
| `git clone failed … not in AllowedHosts` | Add the host to `Atlas__Connectors__Git__AllowedHosts__N` |
| `Scan host exited with code 137` | Child hit the heap limit; raise `Atlas__Scanning__ChildMemoryLimitMb` or narrow the scope |
| `Workspace has N files … above the limit` | Add an `.atlasignore` / exclude paths, or raise `Atlas__Scanning__MaxFiles` |
| PDF returns 503/502 | The `atlas-pdf` sidecar is down or unreachable on `atlas-data`; `docker compose up -d atlas-pdf` |
| `/api/vulnerabilities/status` shows `lastError` | The API has no egress to `osv-vulnerabilities.storage.googleapis.com`; the previous bundle keeps being used |
| 401 on every `/api` call | OIDC enabled; the SPA needs the redirect URI registered at the provider and `ATLAS_AUTH_CLIENT_ID` set |
| 429 responses | Per-IP rate limit; raise `Atlas__Operations__RateLimitPerMinute` behind a shared proxy |

### The UI looks unchanged after an upgrade

Browsers may keep the previous build's `index.html` and assets. Since v0.54.1 the web image sends
`Cache-Control: no-cache` for the shell, so a normal reload picks up new builds; after upgrading *to* v0.54.1 from an
older image, reload once with the cache bypassed (Ctrl+F5 / Cmd+Shift+R) or clear the site data. The version shown in
the sidebar comes from the API and can therefore be newer than the UI you are looking at.

### Roles, in one table

| Who | Can |
| --- | --- |
| viewer (any authenticated user) | read everything except the administrative inventories (API tokens, credentials, tenants) |
| `atlas-analyst` | viewer + create/run assessments, triage, policies per assessment, report own usage (`usage/report`, OTLP) |
| `atlas-admin` | everything: credentials, cost sources, allowlist, prices, budgets, teams, tenant policies, tokens, tenants, deletion |

## 7. Security checklist before sharing an instance

1. OIDC on (`ATLAS_AUTH_ENABLED=true`) with roles: `atlas-admin` (everything), `atlas-analyst` (create/run/triage),
   anyone else authenticated is read-only.
2. TLS termination in front of `:3000` (nginx/Traefik); never publish `:8080` beyond the proxy.
3. `.env` and Postgres dumps stored encrypted; master key rotated only with a planned credential re-entry.
4. Review `AllowedHosts` and `HistoryMonths` for the customer's git servers.

## Kubernetes

The Helm chart in `deploy/helm/atlas` mirrors the compose deployment (see README "Kubernetes (Helm)"). Operational notes:

- `uploads` and `vulndata` PVCs are ReadWriteMany (API writes, worker reads). Without an RWX class, pin API and worker to
  one node or run a single combined node pool.
- Worker replicas can be scaled freely (`worker.replicas`); job claims use SKIP LOCKED. Each worker has its own
  `workspaces` claim only when replicas = 1 — for more replicas switch `persistence.workspaces` to RWX or emptyDir.
- Migrations run on API start (`api.autoMigrate`); with several API replicas the first one migrates, the others wait on
  the migrations history lock.
- Back up the Secret holding `masterKey` together with the database.

## Backup and restore

`deploy/scripts/backup.sh [dir]` → `atlas-<timestamp>/{atlas.dump, uploads.tgz, minio.tgz, env.backup, MANIFEST.txt}`.
`deploy/scripts/restore.sh <folder>` restores in place (stops api/worker/web first). Test restores on a scratch host:
`ATLAS_*` in `.env` must match the dump's master key or stored credentials become unreadable. Schedule the backup with
cron/Task Scheduler; keep at least the last 7 dailies and the `.env` copy encrypted.
