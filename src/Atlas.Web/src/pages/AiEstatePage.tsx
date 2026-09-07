import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, type AiCostSummary, type CostSource, type CostSyncResult, type Credential, type PortfolioAiEstate } from "../api";
import { ErrorBox } from "../components";
import { Donut, HBars, Legend, useTokens } from "../components/charts";
import { Card, EmptyState, PageHeader, Skeleton, Tile } from "../components/ui";
import { useI18n } from "../i18n";
import { ApprovalPill, KindTag } from "./AiEstatePanel";
import { AllowlistCard } from "./AllowlistCard";
import { DeveloperUsageCard } from "./DeveloperUsageCard";
import { ProviderGuide } from "./ProviderGuides";
import { ModelPricesCard } from "./ModelPricesCard";
import { BreakdownCard, BudgetsCard, TeamsCard } from "./BudgetsTab";
import { TelemetryCard } from "./TelemetryCard";
import type { AiUsageSummary, Team } from "../api";

const TABS = ["overview", "budgets", "usage", "spend", "allowlist"] as const;
type EstateTab = (typeof TABS)[number];

function UsageTab() {
  const [rev, setRev] = useState(0);
  const [summary, setSummary] = useState<AiUsageSummary | null>(null);
  useEffect(() => {
    api.getUsageSummary(30).then(setSummary).catch(() => setSummary(null));
  }, [rev]);
  return (
    <>
      <DeveloperUsageCard key={rev} />
      <TelemetryCard usage={summary} />
      <ModelPricesCard onChanged={() => setRev((r) => r + 1)} />
    </>
  );
}

function BudgetsTab() {
  const [teams, setTeams] = useState<Team[]>([]);
  const [summary, setSummary] = useState<AiUsageSummary | null>(null);
  const [rev, setRev] = useState(0);
  useEffect(() => {
    api.listTeams().then(setTeams).catch(() => setTeams([]));
    api.getUsageSummary(30).then(setSummary).catch(() => setSummary(null));
  }, [rev]);
  const bump = () => setRev((r) => r + 1);
  return (
    <>
      <BudgetsCard teams={teams} onChanged={bump} />
      {summary && <BreakdownCard usage={summary} />}
      <TeamsCard teams={teams} onChanged={bump} />
    </>
  );
}

/** Which flags a portfolio row raises, in display order. Exported for tests. */
export function rowFlags(row: { unapproved: number; piiCoLocated: boolean; secretsCoLocated: boolean; retiredModels: number; secretsInMcp: number }): { key: string; tone: "crit" | "warn" | "med" }[] {
  const flags: { key: string; tone: "crit" | "warn" | "med" }[] = [];
  if (row.unapproved > 0) flags.push({ key: "aiestate.flag.unapproved", tone: "crit" });
  if (row.piiCoLocated) flags.push({ key: "aiestate.flag.pii", tone: "warn" });
  if (row.secretsCoLocated) flags.push({ key: "aiestate.flag.secrets", tone: "crit" });
  if (row.retiredModels > 0) flags.push({ key: "aiestate.flag.retired", tone: "med" });
  if (row.secretsInMcp > 0) flags.push({ key: "aiestate.flag.secretMcp", tone: "crit" });
  return flags;
}

/** Portfolio-level inventory: pure, takes the data. */
const KIND_ORDER = ["external-api", "gateway", "local-runtime", "local-inference", "vector-store", "observability", "assistant"];

function kindColor(tk: ReturnType<typeof useTokens>, kind: string): string {
  switch (kind) {
    case "external-api": return tk.accent;
    case "gateway": return tk.medium;
    case "local-runtime":
    case "local-inference": return tk.ok;
    case "vector-store": return tk.low;
    default: return tk.faint;
  }
}

export function EstateOverview({ data }: { data: PortfolioAiEstate }) {
  const { t, formatNumber } = useI18n();
  const tk = useTokens();
  const [filter, setFilter] = useState("");
  const providerBars = useMemo(
    () => data.providers.slice(0, 10).map((p) => ({ name: p.name, value: p.assessments, color: data.allowlistConfigured && p.approved === false ? tk.critical : kindColor(tk, p.kind) })),
    [data, tk],
  );
  const kinds = useMemo(() => {
    const counts = new Map<string, number>();
    for (const p of data.providers) counts.set(p.kind, (counts.get(p.kind) ?? 0) + 1);
    return [...counts.entries()].sort((a, b) => KIND_ORDER.indexOf(a[0]) - KIND_ORDER.indexOf(b[0]));
  }, [data]);
  const kindLabel = (kind: string) => {
    const key = `aiestate.kind.${kind}` as "aiestate.kind.external-api";
    const label = t(key);
    return label === key ? kind : label;
  };
  const visibleProviders = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    return needle ? data.providers.filter((p) => p.name.toLowerCase().includes(needle) || p.id.includes(needle) || p.kind.includes(needle)) : data.providers;
  }, [data, filter]);

  return (
    <>
      <div className="kpis">
        <Tile value={data.assessmentsWithAi} label={t("aiestate.reposUsingAi")} tone="accent" hint={t("aiestate.scanned", { n: data.assessmentsScanned })} />
        <Tile value={data.providers.length} label={t("aiestate.providers")} tone="neutral" />
        <Tile value={data.mcpServers} label={t("aiestate.mcpServers")} tone={data.remoteMcpServers > 0 ? "medium" : "neutral"} hint={data.remoteMcpServers > 0 ? t("aiestate.remote", { n: data.remoteMcpServers }) : undefined} />
        {data.allowlistConfigured && <Tile value={data.unapproved.reduce((s, u) => s + u.count, 0)} label={t("aiestate.unapproved")} tone={data.unapproved.length > 0 ? "critical" : "ok"} />}
        <Tile value={data.retiredModelReferences} label={t("aiestate.retired")} tone={data.retiredModelReferences > 0 ? "medium" : "ok"} />
        <Tile value={data.aiWithPii} label={t("aiestate.aiPii")} tone={data.aiWithPii > 0 ? "high" : "ok"} />
        <Tile value={data.aiWithSecrets} label={t("aiestate.aiSecrets")} tone={data.aiWithSecrets > 0 ? "critical" : "ok"} />
        {data.secretsInMcp > 0 && <Tile value={data.secretsInMcp} label={t("aiestate.secretsMcp")} tone="critical" />}
      </div>

      <div className={`callout ${data.allowlistConfigured ? "ok" : "info"}`}>{data.allowlistConfigured ? t("aiestate.allowlistOn") : t("aiestate.allowlistOff")}</div>

      <div className="grid-2">
        <Card title={t("aiestate.providersTitle")} subtitle={t("aiestate.providersHint")}>
          <HBars height="h-lg" data={providerBars} labelWidth={150} valueFormat={formatNumber} emptyText={t("aiestate.empty.title")} />
        </Card>
        <Card title={t("aiestate.composition")} subtitle={t("aiestate.compositionHint")}>
          <Donut height="h-md" data={kinds.map(([kind, count]) => ({ key: kind, name: kindLabel(kind), value: count }))} colors={(k) => kindColor(tk, k)} centerLabel={t("aiestate.providers")} emptyText={t("aiestate.empty.title")} />
          <Legend items={kinds.map(([kind, count]) => ({ label: kindLabel(kind), color: kindColor(tk, kind), value: count }))} />
          {data.frameworks.length > 0 && (
            <div className="pill-row" style={{ marginTop: "0.9rem" }}>
              <span className="eyebrow">{t("aiestate.frameworks")}</span>
              {data.frameworks.map((f) => <span key={f.name} className="pill accent">{f.name} <b>{f.count}</b></span>)}
            </div>
          )}
          {data.allowlistConfigured && data.unapproved.length > 0 && (
            <div className="pill-row" style={{ marginTop: "0.6rem" }}>
              <span className="eyebrow">{t("aiestate.unapproved")}</span>
              {data.unapproved.map((u) => <span key={u.name} className="pill bad">{u.name} <b>{u.count}</b></span>)}
            </div>
          )}
        </Card>
      </div>

      <Card title={t("aiestate.providers")} actions={<input type="search" placeholder={t("aiestate.search")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ maxWidth: "16rem" }} />}>
          <table>
            <thead>
              <tr>
                <th>{t("aiestate.provider")}</th>
                <th>{t("aiestate.kind")}</th>
                <th className="num">{t("aiestate.repos")}</th>
                <th>{t("aiestate.status")}</th>
              </tr>
            </thead>
            <tbody>
              {visibleProviders.map((p) => (
                <tr key={p.id}>
                  <td className="strong">{p.name}</td>
                  <td><KindTag kind={p.kind} /></td>
                  <td className="num">{p.assessments}</td>
                  <td><ApprovalPill approved={p.approved} allowlist={data.allowlistConfigured} /></td>
                </tr>
              ))}
            </tbody>
          </table>
      </Card>

      <Card title={t("aiestate.rowsTitle")} subtitle={t("aiestate.rowsHint")}>
        <table>
          <thead>
            <tr>
              <th>{t("aiestate.assessment")}</th>
              <th>{t("aiestate.providers")}</th>
              <th>{t("aiestate.frameworks")}</th>
              <th className="num">{t("aiestate.mcpServers")}</th>
              <th>{t("aiestate.flags")}</th>
            </tr>
          </thead>
          <tbody>
            {data.rows.map((row) => {
              const flags = rowFlags(row);
              return (
                <tr key={row.assessmentId}>
                  <td>
                    <Link to={`/assessments/${row.assessmentId}?tab=ai`} className="strong">{row.name}</Link>
                    {row.tags && row.tags.length > 0 && <div className="small muted">{row.tags.join(", ")}</div>}
                  </td>
                  <td className="small">{row.providers.join(", ") || "—"}</td>
                  <td className="small">{row.frameworks.join(", ") || "—"}</td>
                  <td className="num">{row.mcpServers}</td>
                  <td>
                    {flags.length === 0 ? <span className="muted">—</span> : (
                      <div className="flags">{flags.map((f) => <span key={f.key} className={`flag flag-${f.tone}`}>{t(f.key as "aiestate.flag.pii")}</span>)}</div>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </Card>
    </>
  );
}

/** Spend by provider and basis, seats and activity — never summed across bases. Pure. */
export function SpendView({ costs }: { costs: AiCostSummary }) {
  const { t, formatDate } = useI18n();
  const money = (amount: number, currency: string) => `${amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency}`;
  return (
    <>
      <div className="kpis">
        {costs.providers.map((p) => (
          <Tile
            key={`${p.provider}:${p.basis}`}
            value={money(p.total, p.currency)}
            label={`${t(`aiestate.provider.${p.provider}` as "aiestate.provider.openai")} · ${t(`aiestate.basis.${p.basis}` as "aiestate.basis.Estimated")}`}
            tone={p.lastSyncStatus === "Failed" ? "high" : p.basis === "Estimated" ? "neutral" : "accent"}
            hint={p.lastSyncStatus === "Failed" ? p.lastSyncError ?? undefined : p.topDimensions.slice(0, 2).map((d) => `${d.key} ${d.amount.toFixed(2)}`).join(" · ")}
          />
        ))}
        {costs.seats.map((s) => (
          <Tile key={s.provider} value={s.total} label={`${t(`aiestate.provider.${s.provider}` as "aiestate.provider.openai")} · ${t("aiestate.seatsLabel")}`} tone={s.idle > 0 ? "medium" : "ok"} hint={t("aiestate.seatsHint", { active: s.active, idle: s.idle, pending: s.pendingCancellation })} />
        ))}
        {costs.activity.map((a) => (
          <Tile key={`${a.provider}:${a.key}`} value={a.averagePerDay} label={`${t(`aiestate.provider.${a.provider}` as "aiestate.provider.openai")} · ${t(`aiestate.activity.${a.key}` as "aiestate.activity.sessions")}`} tone="neutral" />
        ))}
      </div>
      {costs.providers.some((p) => p.topDimensions.length > 0) && (
        <table>
          <thead>
            <tr>
              <th>{t("aiestate.provider")}</th>
              <th>{t("aiestate.basis")}</th>
              <th className="num">{t("aiestate.total")}</th>
              <th>{t("aiestate.largest")}</th>
              <th>{t("aiestate.lastSync")}</th>
            </tr>
          </thead>
          <tbody>
            {costs.providers.map((p) => (
              <tr key={`${p.provider}:${p.basis}`}>
                <td className="strong">{t(`aiestate.provider.${p.provider}` as "aiestate.provider.openai")}</td>
                <td>{t(`aiestate.basis.${p.basis}` as "aiestate.basis.Estimated")}</td>
                <td className="num">{money(p.total, p.currency)}</td>
                <td className="small">{p.topDimensions.slice(0, 4).map((d) => `${d.key} ${d.amount.toFixed(2)}`).join(" · ") || "—"}</td>
                <td className={`small ${p.lastSyncStatus === "Failed" ? "bad" : ""}`}>{p.lastSyncAtUtc ? formatDate(p.lastSyncAtUtc) : t("aiestate.neverSynced")}{p.lastSyncError ? ` · ${p.lastSyncError}` : ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="muted small">{t("aiestate.spendNote")}</p>
    </>
  );
}

/** The AI estate across the portfolio plus the governance module's cost sources. */
export function AiEstatePage() {
  const { t, lang, formatDate } = useI18n();
  const [data, setData] = useState<PortfolioAiEstate | null | undefined>(undefined);
  const [sources, setSources] = useState<CostSource[] | null>(null);
  const [credentials, setCredentials] = useState<Credential[]>([]);
  const [providers, setProviders] = useState<string[]>(["openai", "anthropic", "github-copilot"]);
  const [error, setError] = useState<string | null>(null);
  const [tag, setTag] = useState("");
  const [params, setParams] = useSearchParams();
  const paramTab = params.get("tab");
  const tab: EstateTab = TABS.includes(paramTab as EstateTab) ? (paramTab as EstateTab) : "overview";
  const goTab = (k: EstateTab) => setParams((p) => { const n = new URLSearchParams(p); if (k === "overview") n.delete("tab"); else n.set("tab", k); return n; }, { replace: true });

  const [provider, setProvider] = useState("openai");
  const [credentialName, setCredentialName] = useState("");
  const [scope, setScope] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [syncDays, setSyncDays] = useState("35");
  const [syncing, setSyncing] = useState(false);
  const [syncResults, setSyncResults] = useState<CostSyncResult[] | null>(null);

  const load = useCallback(() => {
    api.getAiEstate(lang, tag || undefined).then(setData).catch(() => setError(t("error.load")));
    api.listCostSources().then(setSources).catch(() => setSources([]));
  }, [lang, tag, t]);

  useEffect(() => {
    load();
    api.listCredentials().then((r) => setCredentials(r.items)).catch(() => setCredentials([]));
    api.costProviders().then(setProviders).catch(() => {});
  }, [load]);

  async function connect(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setFormError(null);
    setMessage(null);
    try {
      await api.upsertCostSource(provider, { credentialName, scope: scope.trim() || null, enabled: true });
      setMessage(t("aiestate.connected"));
      load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function toggle(source: CostSource) {
    try {
      await api.upsertCostSource(source.provider, { credentialName: source.credentialName, scope: source.scope, enabled: !source.enabled });
      load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    }
  }

  async function disconnect(source: CostSource) {
    if (!window.confirm(t("aiestate.confirmDisconnect"))) return;
    try {
      await api.deleteCostSource(source.provider);
      load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    }
  }

  async function sync() {
    setSyncing(true);
    setSyncResults(null);
    setFormError(null);
    try {
      setSyncResults(await api.syncCosts(Math.max(1, Math.min(400, Number(syncDays) || 35))));
      load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    } finally {
      setSyncing(false);
    }
  }

  if (error) return <ErrorBox message={error} />;

  const costs = data?.costs ?? null;
  const hasInventory = !!data && data.assessmentsWithAi > 0;

  return (
    <>
      <PageHeader
        title={t("aiestate.title")}
        subtitle={t("aiestate.subtitle")}
        actions={
          <>
            <input type="search" placeholder={t("portfolio.tagFilter")} value={tag} onChange={(e) => setTag(e.target.value)} style={{ maxWidth: "12rem" }} />
            <a className="button" href={api.portfolioReportUrl(lang, tag || undefined)} target="_blank" rel="noreferrer">{t("aiestate.portfolioReport")} ↗</a>
          </>
        }
      />

      <div className="tabs" role="tablist">
        {TABS.map((k) => (
          <button key={k} type="button" role="tab" aria-selected={tab === k} className={`tab${tab === k ? " active" : ""}`} onClick={() => goTab(k)}>
            {t(`aiestate.tab.${k}` as "aiestate.tab.overview")}
            {k === "overview" && data && data.unapproved.length > 0 && <span className="tab-badge">{data.unapproved.length}</span>}
          </button>
        ))}
      </div>

      {tab === "overview" && (data === undefined ? (
        <>
          <Skeleton kind="tile" count={6} />
          <Skeleton kind="block" />
        </>
      ) : hasInventory ? (
        <EstateOverview data={data!} />
      ) : (
        <EmptyState glyph="✦" title={t("aiestate.empty.title")} text={t("aiestate.empty.text")} action={<Link to="/assessments" className="button primary">{t("nav.assessments")} →</Link>} />
      ))}

      {tab === "allowlist" && <AllowlistCard onChanged={load} />}

      {tab === "spend" && (<>
      <Card title={t("aiestate.spend")} subtitle={t("aiestate.spendHint", { days: costs?.days ?? 30 })}>
        {costs && costs.providers.length + costs.seats.length + costs.activity.length > 0 ? <SpendView costs={costs} /> : <p className="muted">{t("aiestate.noSpend")}</p>}
      </Card>

      <Card
        title={t("aiestate.sources")}
        subtitle={t("aiestate.sourcesHint")}
        actions={
          sources && sources.some((s) => s.enabled) && (
            <span className="row">
              <label className="small muted row" style={{ gap: "0.3rem" }}>
                <input type="number" min={1} max={400} value={syncDays} onChange={(e) => setSyncDays(e.target.value)} style={{ width: "4.5rem" }} /> {t("aiestate.syncDays")}
              </label>
              <button type="button" className="button primary" disabled={syncing} onClick={() => void sync()}>
                {syncing ? t("aiestate.syncing") : `↻ ${t("aiestate.sync")}`}
              </button>
            </span>
          )
        }
      >
        {syncResults && (
          <ul className="sync-results">
            {syncResults.map((r) => (
              <li key={r.provider} className={r.succeeded ? "ok" : "bad"}>
                {r.succeeded ? t("aiestate.syncResult", { provider: r.provider, facts: r.facts, seats: r.seats }) : t("aiestate.syncFailed", { provider: r.provider, error: r.error ?? "" })}
              </li>
            ))}
          </ul>
        )}
        {sources === null ? (
          <Skeleton count={2} />
        ) : sources.length === 0 ? (
          <p className="muted">{t("aiestate.noSources")}</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>{t("aiestate.provider")}</th>
                <th>{t("aiestate.credential")}</th>
                <th>{t("aiestate.scope")}</th>
                <th>{t("aiestate.lastSync")}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {sources.map((s) => (
                <tr key={s.provider} className={s.enabled ? "" : "triaged"}>
                  <td className="strong">{t(`aiestate.provider.${s.provider}` as "aiestate.provider.openai")}</td>
                  <td className="mono">{s.credentialName}</td>
                  <td className="mono">{s.scope ?? "—"}</td>
                  <td className="small">
                    {s.lastSyncAtUtc ? (
                      <>
                        <span className={`pill ${s.lastSyncStatus === "Failed" ? "bad" : "ok"}`}>{s.lastSyncStatus}</span> {formatDate(s.lastSyncAtUtc)} · {t("aiestate.facts", { n: s.lastSyncFacts })}
                        {s.lastSyncError && <div className="bad">{s.lastSyncError}</div>}
                      </>
                    ) : (
                      <span className="muted">{t("aiestate.neverSynced")}</span>
                    )}
                  </td>
                  <td>
                    <div className="actions">
                      <button type="button" className="button small" onClick={() => void toggle(s)}>{s.enabled ? t("aiestate.disable") : t("aiestate.enable")}</button>
                      <button type="button" className="button small danger" onClick={() => void disconnect(s)}>{t("aiestate.disconnect")}</button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <form className="form source-form" onSubmit={connect}>
          <div className="discover-row">
            <label>
              <span>{t("aiestate.provider")}</span>
              <select value={provider} onChange={(e) => setProvider(e.target.value)}>
                {providers.map((p) => <option key={p} value={p}>{t(`aiestate.provider.${p}` as "aiestate.provider.openai")}</option>)}
              </select>
            </label>
            <label style={{ flex: 1 }}>
              <span>{t("aiestate.credential")}</span>
              <select value={credentialName} onChange={(e) => setCredentialName(e.target.value)} required>
                <option value="">—</option>
                {credentials.map((c) => <option key={c.name} value={c.name}>{c.name}{c.description ? ` · ${c.description}` : ""}</option>)}
              </select>
              <small className="muted">{t("aiestate.credentialHint")} <Link to="/credentials">{t("nav.credentials")} →</Link></small>
            </label>
            <label style={{ flex: 1 }}>
              <span>{t("aiestate.scope")}</span>
              <input className="mono" value={scope} onChange={(e) => setScope(e.target.value)} placeholder={provider === "github-copilot" ? "my-org" : ""} required={provider === "github-copilot"} />
              <small className="muted">{t("aiestate.scopeHint")}</small>
            </label>
          </div>
          <ProviderGuide provider={provider} />
          {formError && <ErrorBox message={formError} />}
          {message && <p className="banner ok">{message}</p>}
          <div className="actions">
            <button type="submit" className="button primary" disabled={busy || !credentialName}>{busy ? t("aiestate.connecting") : t("aiestate.connect")}</button>
            <span className="muted small">{t("aiestate.adminOnly")}</span>
          </div>
        </form>
      </Card>
      </>)}

      {tab === "usage" && <UsageTab />}

      {tab === "budgets" && <BudgetsTab />}
    </>
  );
}
