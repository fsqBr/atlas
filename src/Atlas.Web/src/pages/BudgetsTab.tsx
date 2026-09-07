import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type AiUsageSummary, type Budget, type BudgetAlert, type KnownActor, type Team } from "../api";
import { ErrorBox } from "../components";
import { Card, Tile } from "../components/ui";
import { useI18n } from "../i18n";

export const SCOPES = ["tenant", "team", "provider", "model", "actor", "tool"] as const;
export type Scope = (typeof SCOPES)[number];

function money(v: number, lang: string): string {
  return `$${v.toLocaleString(lang, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

/** Pure view: budgets as progress bars with spent, projected and state. */
export function BudgetsView({ budgets, onEdit, onRemove }: { budgets: Budget[]; onEdit: (b: Budget) => void; onRemove: (b: Budget) => void }) {
  const { t, lang } = useI18n();
  if (budgets.length === 0) return <p className="muted">{t("budgets.none")}</p>;
  return (
    <div className="budgets" data-testid="budgets-view">
      {budgets.map((b) => (
        <div key={b.id} className={`budget budget-${b.state}${b.enabled ? "" : " budget-off"}`}>
          <div className="budget-head">
            <div>
              <strong>{b.name ?? t(`budgets.scope.${b.scope}` as "budgets.scope.tenant")}</strong>
              {b.scopeKey && <code className="budget-key">{b.scopeKey}</code>}
              <span className={`pill ${b.state === "over" ? "bad" : b.state === "warn" ? "warn" : "ok"}`}>{t(`budgets.state.${b.state}` as "budgets.state.ok")}</span>
              {!b.enabled && <span className="pill soft">{t("budgets.disabled")}</span>}
            </div>
            <div className="actions">
              <button type="button" className="button small" onClick={() => onEdit(b)}>{t("prices.edit")}</button>
              <button type="button" className="button small danger" onClick={() => onRemove(b)}>{t("prices.remove")}</button>
            </div>
          </div>
          <div className="budget-bar" role="progressbar" aria-valuenow={Math.min(100, b.percent)} aria-valuemin={0} aria-valuemax={100} aria-label={b.label}>
            <span className="spent" style={{ width: `${Math.min(100, b.percent)}%` }} />
            {b.projectedPercent > b.percent && <span className="projected" style={{ left: `${Math.min(100, b.percent)}%`, width: `${Math.max(0, Math.min(100, b.projectedPercent) - Math.min(100, b.percent))}%` }} />}
            {[50, 80].map((m) => <i key={m} className="mark" style={{ left: `${m}%` }} />)}
          </div>
          <div className="budget-foot small">
            <span><b>{money(b.spentMonthToDate, lang)}</b> {t("budgets.of")} {money(b.monthlyAmount, lang)} · {b.percent.toLocaleString(lang, { maximumFractionDigits: 0 })}%</span>
            <span className="muted">{t("budgets.projected", { amount: money(b.projectedMonth, lang), pct: b.projectedPercent.toLocaleString(lang, { maximumFractionDigits: 0 }) })} · {t("usage.fc.mtdHint", { d: b.daysElapsed, n: b.daysInMonth })}</span>
          </div>
        </div>
      ))}
    </div>
  );
}

export function AlertsList({ alerts }: { alerts: BudgetAlert[] }) {
  const { t, formatDate } = useI18n();
  if (alerts.length === 0) return <p className="muted small">{t("budgets.noAlerts")}</p>;
  return (
    <ul className="alerts">
      {alerts.map((a) => (
        <li key={a.id} className={a.kind === "anomaly" ? "anomaly" : "threshold"}>
          <span className={`pill ${a.kind === "anomaly" ? "warn" : a.percent !== null && a.percent >= 100 ? "bad" : "accent"}`}>{t(`budgets.kind.${a.kind}` as "budgets.kind.threshold")}</span>
          <span className="alert-msg">{a.message}</span>
          <span className="muted small">{formatDate(a.createdAtUtc)}{a.delivered ? ` · ${t("budgets.delivered")}` : a.deliveryError ? ` · ${t("budgets.notDelivered")}: ${a.deliveryError}` : ""}</span>
        </li>
      ))}
    </ul>
  );
}

type Draft = { id: string | null; scope: Scope; scopeKey: string; monthlyAmount: string; name: string; enabled: boolean };
const emptyBudget: Draft = { id: null, scope: "tenant", scopeKey: "", monthlyAmount: "", name: "", enabled: true };

export function BudgetsCard({ teams, onChanged }: { teams: Team[]; onChanged?: () => void }) {
  const { t } = useI18n();
  const [budgets, setBudgets] = useState<Budget[] | null>(null);
  const [alerts, setAlerts] = useState<BudgetAlert[]>([]);
  const [draft, setDraft] = useState<Draft>(emptyBudget);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    api.listBudgets().then(setBudgets).catch((e) => setError(e instanceof Error ? e.message : String(e)));
    api.budgetAlerts(30).then(setAlerts).catch(() => setAlerts([]));
  }, []);
  useEffect(load, [load]);

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const amount = Number(draft.monthlyAmount.replace(",", "."));
    if (!Number.isFinite(amount) || amount <= 0) {
      setError(t("budgets.invalidAmount"));
      return;
    }
    if (draft.scope !== "tenant" && !draft.scopeKey.trim()) {
      setError(t("budgets.needKey"));
      return;
    }
    setBusy(true);
    try {
      await api.upsertBudget({ id: draft.id, scope: draft.scope, scopeKey: draft.scope === "tenant" ? null : draft.scopeKey.trim(), monthlyAmount: amount, name: draft.name.trim() || null, enabled: draft.enabled });
      setDraft(emptyBudget);
      load();
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function remove(b: Budget) {
    if (!window.confirm(t("budgets.confirmRemove", { label: b.label }))) return;
    try {
      await api.deleteBudget(b.id);
      load();
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  const keyPlaceholder = draft.scope === "team" ? (teams[0]?.name ?? "Payments") : draft.scope === "provider" ? "anthropic" : draft.scope === "model" ? "claude-opus" : draft.scope === "actor" ? "ana" : draft.scope === "tool" ? "claude-code" : "";

  return (
    <Card title={t("budgets.title")} subtitle={t("budgets.hint")}>
      {budgets === null ? <p className="muted">…</p> : <BudgetsView budgets={budgets} onEdit={(b) => setDraft({ id: b.id, scope: b.scope as Scope, scopeKey: b.scopeKey ?? "", monthlyAmount: String(b.monthlyAmount), name: b.name ?? "", enabled: b.enabled })} onRemove={(b) => void remove(b)} />}

      <form className="form price-form" onSubmit={save}>
        <h4 className="sub">{draft.id ? t("budgets.editing") : t("budgets.add")}</h4>
        <div className="price-grid">
          <label>
            <span>{t("budgets.scopeLabel")}</span>
            <select value={draft.scope} onChange={(e) => setDraft((d) => ({ ...d, scope: e.target.value as Scope, scopeKey: "" }))}>
              {SCOPES.map((s) => <option key={s} value={s}>{t(`budgets.scope.${s}` as "budgets.scope.tenant")}</option>)}
            </select>
          </label>
          {draft.scope !== "tenant" && (
            <label>
              <span>{t(`budgets.key.${draft.scope}` as "budgets.key.team")}</span>
              {draft.scope === "team" && teams.length > 0 ? (
                <select value={draft.scopeKey} onChange={(e) => setDraft((d) => ({ ...d, scopeKey: e.target.value }))} required>
                  <option value="">—</option>
                  {teams.map((tm) => <option key={tm.id} value={tm.name}>{tm.name}</option>)}
                </select>
              ) : (
                <input className="mono" value={draft.scopeKey} onChange={(e) => setDraft((d) => ({ ...d, scopeKey: e.target.value }))} placeholder={keyPlaceholder} required />
              )}
            </label>
          )}
          <label>
            <span>{t("budgets.amount")}</span>
            <input inputMode="decimal" value={draft.monthlyAmount} onChange={(e) => setDraft((d) => ({ ...d, monthlyAmount: e.target.value }))} placeholder="500" required />
          </label>
          <label className="wide">
            <span>{t("budgets.name")}</span>
            <input value={draft.name} onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))} placeholder={t("budgets.namePlaceholder")} />
          </label>
          <label className="check">
            <input type="checkbox" checked={draft.enabled} onChange={(e) => setDraft((d) => ({ ...d, enabled: e.target.checked }))} /> <span>{t("budgets.enabled")}</span>
          </label>
        </div>
        {error && <ErrorBox message={error} />}
        <div className="actions">
          <button type="submit" className="button primary" disabled={busy}>{busy ? t("prices.saving") : t("budgets.save")}</button>
          {draft.id && <button type="button" className="button" onClick={() => setDraft(emptyBudget)}>{t("prices.clear")}</button>}
          <span className="muted small">{t("budgets.thresholds")}</span>
        </div>
      </form>

      <h4 className="sub" style={{ marginTop: "1.2rem" }}>{t("budgets.alerts")}</h4>
      <AlertsList alerts={alerts} />
      <p className="muted small">{t("budgets.channels")}</p>
    </Card>
  );
}

export function TeamsCard({ teams, onChanged }: { teams: Team[]; onChanged: () => void }) {
  const { t } = useI18n();
  const [actors, setActors] = useState<KnownActor[]>([]);
  const [name, setName] = useState("");
  const [members, setMembers] = useState<string[]>([]);
  const [extra, setExtra] = useState("");
  const [channels, setChannels] = useState({ webhookUrl: "", slackWebhookUrl: "", teamsWebhookUrl: "" });
  const [editing, setEditing] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.knownActors().then(setActors).catch(() => setActors([]));
  }, [teams]);

  function edit(team: Team) {
    setEditing(team.id);
    setName(team.name);
    setMembers(team.members);
    setChannels({ webhookUrl: team.webhookUrl ?? "", slackWebhookUrl: team.slackWebhookUrl ?? "", teamsWebhookUrl: team.teamsWebhookUrl ?? "" });
  }

  function reset() {
    setEditing(null);
    setName("");
    setMembers([]);
    setExtra("");
    setChannels({ webhookUrl: "", slackWebhookUrl: "", teamsWebhookUrl: "" });
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const all = [...members, ...extra.split(/[,\n]/).map((s) => s.trim()).filter(Boolean)];
    try {
      await api.upsertTeam({ id: editing, name: name.trim(), members: all, webhookUrl: channels.webhookUrl.trim() || null, slackWebhookUrl: channels.slackWebhookUrl.trim() || null, teamsWebhookUrl: channels.teamsWebhookUrl.trim() || null });
      reset();
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function remove(team: Team) {
    if (!window.confirm(t("teams.confirmRemove", { name: team.name }))) return;
    try {
      await api.deleteTeam(team.id);
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  const toggle = (actor: string) => setMembers((m) => (m.includes(actor) ? m.filter((x) => x !== actor) : [...m, actor]));
  const unassigned = actors.filter((a) => !a.team);

  return (
    <Card title={t("teams.title")} subtitle={t("teams.hint")}>
      {teams.length === 0 ? <p className="muted">{t("teams.none")}</p> : (
        <table className="compact">
          <thead>
            <tr><th>{t("teams.name")}</th><th>{t("teams.members")}</th><th></th></tr>
          </thead>
          <tbody>
            {teams.map((tm) => (
              <tr key={tm.id}>
                <td><strong>{tm.name}</strong></td>
                <td>{tm.members.map((m) => <span key={m} className="kind">{m}</span>)}{(tm.slackWebhookUrl || tm.teamsWebhookUrl || tm.webhookUrl) && <span className="pill accent" title={t("teams.ownChannels")}>{t("teams.alerts")}</span>}</td>
                <td>
                  <div className="actions">
                    <button type="button" className="button small" onClick={() => edit(tm)}>{t("prices.edit")}</button>
                    <button type="button" className="button small danger" onClick={() => void remove(tm)}>{t("prices.remove")}</button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {unassigned.length > 0 && <p className="small muted">{t("teams.unassigned", { n: unassigned.length })}</p>}

      <form className="form price-form" onSubmit={save}>
        <h4 className="sub">{editing ? t("teams.editing") : t("teams.add")}</h4>
        <label>
          <span>{t("teams.name")}</span>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Payments" required />
        </label>
        <span className="small muted">{t("teams.pick")}</span>
        <div className="member-picker">
          {actors.map((a) => (
            <label key={a.actor} className={`chip${members.includes(a.actor) ? " on" : ""}`} title={a.team ? t("teams.inTeam", { team: a.team }) : ""}>
              <input type="checkbox" checked={members.includes(a.actor)} onChange={() => toggle(a.actor)} />
              <code>{a.actor}</code>{a.team && a.team !== name && <small className="muted"> · {a.team}</small>}
            </label>
          ))}
          {actors.length === 0 && <span className="muted small">{t("teams.noActors")}</span>}
        </div>
        <label>
          <span>{t("teams.extra")}</span>
          <input className="mono" value={extra} onChange={(e) => setExtra(e.target.value)} placeholder="svc:payments-*, maria" />
          <small className="muted">{t("teams.extraHint")}</small>
        </label>
        <details className="more">
          <summary>{t("teams.channels")}</summary>
          <p className="small muted">{t("teams.channelsHint")}</p>
          <div className="price-grid">
            <label><span>Slack</span><input className="mono" value={channels.slackWebhookUrl} onChange={(e) => setChannels((c) => ({ ...c, slackWebhookUrl: e.target.value }))} placeholder="https://hooks.slack.com/services/…" /></label>
            <label><span>Microsoft Teams</span><input className="mono" value={channels.teamsWebhookUrl} onChange={(e) => setChannels((c) => ({ ...c, teamsWebhookUrl: e.target.value }))} placeholder="https://prod-xx.logic.azure.com/workflows/…" /></label>
            <label><span>Webhook</span><input className="mono" value={channels.webhookUrl} onChange={(e) => setChannels((c) => ({ ...c, webhookUrl: e.target.value }))} placeholder="https://…" /></label>
          </div>
        </details>
        {error && <ErrorBox message={error} />}
        <div className="actions">
          <button type="submit" className="button primary" disabled={!name.trim()}>{t("teams.save")}</button>
          {editing && <button type="button" className="button" onClick={reset}>{t("prices.clear")}</button>}
        </div>
      </form>
    </Card>
  );
}

/** Spend by team, provider and tool from the summary — the whole-company view. */
export function BreakdownCard({ usage }: { usage: AiUsageSummary }) {
  const { t, lang } = useI18n();
  const $ = (v: number) => money(v, lang);
  const fmt = (n: number) => (n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n));
  return (
    <Card title={t("breakdown.title")} subtitle={t("breakdown.hint", { days: usage.days })} actions={<a className="button small" href={api.usageExportUrl(usage.days)}>⬇ {t("usage.exportCsv")}</a>}>
      <div className="tiles">
        <Tile tone="accent" label={t("breakdown.teams")} value={usage.byTeam.filter((x) => x.team !== "(unassigned)").length} hint={t("breakdown.teamsHint")} />
        <Tile tone="neutral" label={t("breakdown.providers")} value={usage.byProvider.length} hint={usage.byProvider.slice(0, 3).map((p) => p.provider).join(", ")} />
        <Tile tone="neutral" label={t("breakdown.tools")} value={usage.byTool.length} hint={usage.byTool.slice(0, 3).map((p) => p.tool).join(", ")} />
      </div>
      <div className="usage-grid">
        <div className="table-scroll">
          <h4 className="sub">{t("breakdown.byTeam")}</h4>
          <table className="compact">
            <thead><tr><th>{t("teams.team")}</th><th className="num">{t("teams.members")}</th><th className="num">{t("usage.tokens")}</th><th className="num">{t("usage.cost")}</th><th className="num">{t("usage.fc.mtdShort")}</th><th className="num">{t("usage.fc.monthShort")}</th></tr></thead>
            <tbody>
              {usage.byTeam.map((x) => (
                <tr key={x.team} className={x.team === "(unassigned)" ? "muted" : undefined}>
                  <td>{x.team === "(unassigned)" ? <i>{t("breakdown.unassigned")}</i> : <strong>{x.team}</strong>}</td>
                  <td className="num">{x.members}</td>
                  <td className="num">{fmt(x.tokens)}</td>
                  <td className="num">{$(x.estimatedCost)}</td>
                  <td className="num">{$(x.monthToDateCost)}</td>
                  <td className="num">{$(x.projectedMonthCost)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="table-scroll">
          <h4 className="sub">{t("breakdown.byProvider")}</h4>
          <table className="compact">
            <thead><tr><th>{t("breakdown.provider")}</th><th className="num">{t("usage.tokens")}</th><th className="num">{t("usage.cost")}</th><th className="num">{t("usage.devs")}</th></tr></thead>
            <tbody>
              {usage.byProvider.map((p) => (
                <tr key={p.provider}>
                  <td className="mono small">{p.provider}{p.unpricedTokens > 0 && <span className="pill warn" style={{ marginLeft: "0.4rem" }}>{t("usage.unpricedShort")}</span>}</td>
                  <td className="num">{fmt(p.tokens)}</td>
                  <td className="num">{$(p.estimatedCost)}</td>
                  <td className="num">{p.actors}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <h4 className="sub">{t("breakdown.byTool")}</h4>
          <table className="compact">
            <thead><tr><th>{t("breakdown.tool")}</th><th>{t("breakdown.source")}</th><th className="num">{t("usage.tokens")}</th><th className="num">{t("usage.cost")}</th><th className="num">{t("usage.devs")}</th></tr></thead>
            <tbody>
              {usage.byTool.map((x) => (
                <tr key={x.tool}>
                  <td className="mono small">{x.tool}</td>
                  <td><span className="kind">{x.source}</span></td>
                  <td className="num">{fmt(x.tokens)}</td>
                  <td className="num">{$(x.estimatedCost)}</td>
                  <td className="num">{x.actors}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </Card>
  );
}
