# Security Policy

**Product:** Atlas Platform — Enterprise Software Intelligence
**Last updated:** 2026-08-28

---

## 1. Responsible Disclosure

We take security reports seriously. Atlas analyzes source code and stores security findings; a vulnerability in Atlas can expose our customers' most sensitive engineering data.

- **Contact:** `security@<company-domain>` <!-- TODO: replace with monitored mailbox before first release -->
- **PGP key:** <!-- TODO: publish fingerprint and key before first release -->
- **Please include:** affected component/version, reproduction steps, impact assessment, and any proof-of-concept. Do not access data that is not yours; do not test against instances you do not own or have written authorization to test.
- **Our commitment (SLA):**
  - Acknowledgement within **3 business days**.
  - Initial triage and severity assessment within **10 business days**.
  - Coordinated disclosure: we ask for a **90-day** window before public disclosure, negotiable for complex fixes.
- Good-faith research within these rules will not result in legal action.

## 2. Supported Versions

| Version | Supported |
|---|---|
| Latest minor release | Yes — security fixes |
| Previous minor release | Critical fixes only, for 6 months after supersession |
| Anything older | No |

Self-hosted customers are notified of security releases through the release channel and are expected to upgrade within the support window. Security-relevant changes are always called out explicitly in release notes.

## 3. Product Security Model

Atlas is designed around the assumption that **Atlas itself is a high-value target**: it holds read credentials for an organization's repositories and a consolidated map of that organization's vulnerabilities, secrets locations, and sensitive-data flows.

### 3.1 Analysis without execution

The defining security rule of the product:

> **Atlas analyzes customer code as data, never as a program.**

Concretely:

- No `nuget restore`, `dotnet build`, or execution of MSBuild custom tasks/targets from analyzed repositories in V1. Project and dependency information is read from `.csproj`/`.sln`/`packages.config`/lock files **as XML/text**, not by invoking the build toolchain of the analyzed repository.
- Semantic analysis uses design-time techniques (tiered analysis: syntactic always; design-time build only where it does not execute repository-controlled logic; anything beyond runs sandboxed).
- Test coverage is obtained by **ingesting existing coverage reports** (coverlet/Cobertura/OpenCover), never by running customer tests.
- Analyzed repositories are treated as **hostile input**: symlink targets are validated against the workspace root (realpath checks), archives are size/entry-bounded, and file paths from repositories are treated as untrusted in every sink (UI, PDF, logs).
- Scan workers run with **no outbound network access** (egress deny by default). Git fetch runs in a separate process/container from analysis.
- Each scan job runs in an **isolated child process** with memory/CPU/time caps.

### 3.2 Self-hosted first

The complete platform runs inside customer infrastructure. Customer source code never needs to leave the customer's environment. The managed cloud offering is optional and adds tenant-isolation controls on top of, not instead of, this model.

### 3.2a AI providers — opt-in egress

Atlas can use an LLM for business-rule discovery. This is the only feature that sends source code outside the
deployment, and it is **off by default**:

- An administrator must configure a provider **and** enable the switch under *Settings → AI*; both are audited.
- Only the bodies of selected methods are sent (bounded count and size), with file and member names — never whole
  repositories, binaries, secrets findings or PII findings.
- Requests go from the worker straight to the configured base URL (your provider account, an Azure region, or a
  local Ollama — in which case nothing leaves the host).
- Keys are stored as AES-256-GCM envelopes under `ATLAS_MASTER_KEY`, are write-only through the API, and are
  discarded when the provider changes.
- AI output is labelled with model and confidence, stored separately, and never affects scores or findings.

### 3.2b Tenant isolation

Tenant scope is resolved once per request from the identity token and applied as a global EF Core query filter on every
tenant-scoped table. Reading a row of another tenant returns 404, never the row. A request whose tenant could not be
resolved fails closed (the context throws instead of defaulting to "everything"); only the worker and API background
services run in system scope. Tenant registration (`/api/tenants`) is admin-only and audited.

### 3.2c Tier 2 restore (opt-in)

`Atlas:Scanning:Tier2:Enabled` runs `dotnet restore` on the assessed repository to obtain project-accurate references.
Restore is MSBuild evaluation: repository-provided props/targets can execute logic. Atlas therefore keeps Tier 2 off by
default, runs it only inside the scan-host child process (heap cap, wall-clock timeout, no database access), with
`--ignore-failed-sources` and telemetry disabled, and documents that it should be enabled only for repositories the
operator trusts or in an isolated worker pool.

### 3.3 What Atlas stores — and what it never stores

| Atlas stores | Atlas NEVER stores |
|---|---|
| Findings with evidence *references* (repo, commit, file, span, symbol) | Raw secret values discovered in code |
| Secret findings as **keyed HMAC fingerprints** + type + location | Full source code copies beyond the ephemeral scan workspace, unless explicitly enabled |
| PII findings as type + location + confidence | The personal data itself (CPF/CNPJ/emails/etc. are never persisted in clear text) |
| Aggregated metrics and snapshots | Connector credentials in plaintext (always envelope-encrypted, value lives in the secret store) |
| Audit log of sensitive operations | AI payloads beyond the auditable interaction log retention window |

Evidence snippets pass through **redaction of secret/PII matches before persistence**.

## 4. Sensitive Data Policy

### 4.1 Discovered secrets

- Finding records contain: secret **type**, **location**, **confidence**, and a fingerprint computed as **HMAC-SHA256 keyed with a per-installation key** (a plain hash of low-entropy secrets is brute-forceable if the database leaks).
- A masked preview (at most first 4 characters) exists only where strictly necessary and is gated behind a dedicated permission and audit-logged on read.
- The raw secret value is never written to the database, logs, exports, or AI prompts.

### 4.2 Discovered PII

Rule: **location yes, value no.** PII findings record file/line/type/confidence — never the CPF, CNPJ, email, or health data itself. This keeps the Atlas database out of scope as a personal-data repository to the maximum extent possible (LGPD).

### 4.3 Retention

- **Defaults:** scan workspaces — deleted at job completion (success or failure); raw scan artifacts — 90 days; findings and aggregated snapshots — retained while the assessment exists; AI interaction logs — 180 days.
- All retention windows are **configurable per installation**; deletion is **hard delete, asynchronous, per partition/tenant, and auditable** (generalized soft delete conflicts with data-minimization and LGPD elimination duties).

## 5. Secure Deployment Guide (self-hosted)

Operators of self-hosted Atlas must follow these baselines; the default `docker compose` distribution is configured to make the secure path the easy path:

- **TLS at the edge.** Terminate TLS at a reverse proxy in front of `atlas-web`/`atlas-api`. Documented reference configuration ships with the product. No plaintext HTTP outside localhost trials.
- **Database credentials.** The PostgreSQL password is **generated at first boot** — there is no default password. Internal service ports (PostgreSQL, object storage) are **not published** to the host by default.
- **Connector credentials** (GitHub/Azure DevOps/GitLab tokens) are envelope-encrypted (AES-GCM) with a master key supplied via environment/mounted file — never stored in `appsettings.json`. Pluggable `ISecretStore` providers: local envelope (default), Azure Key Vault, HashiCorp Vault. Prefer OAuth/app installations and short-lived, **read-only** tokens; Atlas does not need repository write access in V1.
- **Worker network.** Scan workers must run with **no outbound egress**. The only network-touching component for source acquisition is the fetch process, separated from analysis.
- **Containers** run as non-root with read-only root filesystems where possible; images are minimal (distroless/chiseled).
- **Backups** of PostgreSQL and object storage must be encrypted at rest; treat backups with the same sensitivity as the live findings database — a findings backup is a consolidated attack map.
- **Authentication.** OIDC against the customer IdP (Entra ID, Keycloak, etc.). No unauthenticated mode except an explicit development flag that must never be set in production.
- **Audit log** is append-only and captures: logins, reads of secret findings, report exports, connector credential changes, retention/config changes.
- **AI is off by default** (`provider=None`). Enabling an external AI provider is an explicit configuration act; when enabled, every payload sent is auditable. Self-hosted/local (OpenAI-compatible, e.g. Ollama) endpoints are supported so code never leaves the environment.

## 6. Supply Chain Security

- **SBOM** (CycloneDX or SPDX) generated and published **per release**, for every container image.
- Container images **signed** (cosign/notation); customers verify signatures as part of the documented install procedure.
- Dependencies are pinned and updated through a reviewed process; base images follow a defined refresh cadence; the product's own dependencies are scanned for known vulnerabilities in CI.
- Build provenance: releases are built from tagged commits by CI, not from developer machines.

## 7. LGPD (and equivalent data-protection regimes)

- **Roles:** for personal data contained in analyzed code and in findings, the **customer is the controller** and **Atlas (self-hosted) runs entirely as the customer's tool**; in the managed cloud offering, the Atlas operator acts as **processor (operador)** under the customer's instructions. A DPA governs the cloud offering.
- **Minimization by design:** PII detections store location, not value (.2); secrets store fingerprints, not values (.1); source code is not retained beyond the ephemeral workspace.
- **Elimination:** hard-delete workflows exist per tenant, per assessment, and per repository; deletion jobs are auditable and propagate to object storage and backups within the documented backup-rotation window.
- **Data residency:** self-hosted deployments keep all data inside customer infrastructure by construction.

## 8. Related Documents


## Connector credentials — as implemented (V0.5)

- Table `atlas.connector_credentials`: name (unique per tenant), optional username, description and an AES-256-GCM
  envelope `[version][nonce][tag][ciphertext]` under `Atlas:Secrets:MasterKeyBase64` (`ATLAS_MASTER_KEY`).
  The key exists only in the environment of the API and the worker; losing it makes stored credentials
  unrecoverable — rotate instead of recovering.
- Write-only API: `PUT /api/credentials/{name}` stores or rotates, `GET /api/credentials` returns metadata only,
  `DELETE` is refused (409) while an assessment references the name. No endpoint returns a secret.
- Assessments reference credentials by name. The git connector resolves the value at clone time and hands it to
  git through a temporary `GIT_ASKPASS` helper reading process-scoped environment variables: the secret never
  appears in the command line, remote URL, git config, workspace or logs; git's error output is redacted before
  it is persisted. Host credential helpers are disabled for the clone (`-c credential.helper=`).

## Container containment — as implemented (V0.5)

- Two Docker networks: `atlas-data` is `internal: true` (no route to the internet) and holds Postgres, MinIO and
  the Gotenberg PDF sidecar; `atlas-app` carries web → api and gives api/worker their egress (git clones, provider
  APIs, OSV sync). Only the web UI (:3000) and the API (:8080) are published on the host.
- `atlas-api` and `atlas-worker`: read-only root filesystem, `cap_drop: ALL`, `no-new-privileges`, memory and pid
  limits. Writable paths are explicit: a tmpfs `/tmp`, the OSV bundle volume (api rw / worker ro) and the clone
  workspace volume (`/var/atlas/workspaces`, worker only, garbage-collected by lease). Local sources are mounted
  read-only. nginx keeps only the capabilities it needs to bind :80 and drop privileges.
- Still open (V1 hardening): per-scan child-process isolation with its own cgroup, seccomp profile, OIDC on the API
  and UI, and network egress allow-listing for the worker (git hosts only).

## Scan isolation and egress — as implemented (V0.5)

- Per-run child process (`Atlas.Worker scan-host`): no database access, no job lease, heap hard limit and wall-clock
  timeout; killed as a process tree on timeout. The worker only exchanges JSON files with it (request/outcome).
- Clone egress allow-list (`Atlas:Connectors:Git:AllowedHosts`), enforced before git starts; embedded URL credentials
  refused. Provider connectors (GitHub, Azure DevOps, GitLab) only reach their configured base URLs.
- Optional OIDC: JWT bearer validation against the configured authority; `/api/*` gated, health probes open; the SPA
  is a public client (authorization code + PKCE), tokens never touch the server side except as bearer headers
  (query-string token accepted only for the report/PDF navigations).
- Still open for V1: seccomp/AppArmor profile for the scan host, per-tenant RBAC, audit log of API calls.
