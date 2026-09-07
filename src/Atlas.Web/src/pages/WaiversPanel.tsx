import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { api, type Suppression, type SuppressionPolicy } from "../api";
import { ErrorBox } from "../components";
import { Card, EmptyState, Skeleton, Tile } from "../components/ui";
import { useI18n } from "../i18n";
import { WaiversCard } from "./WaiversCard";

const AUTHOR_KEY = "atlas.triage.author";

export type WaiverState = "active" | "expired" | "revoked";

/** A waiver is active while not revoked and not past its expiry; expired ones reopened (or will reopen) on their own. */
export function waiverState(s: { revokedAtUtc: string | null; expiresAtUtc: string | null }, now = Date.now()): WaiverState {
  if (s.revokedAtUtc) return "revoked";
  if (s.expiresAtUtc && new Date(s.expiresAtUtc).getTime() <= now) return "expired";
  return "active";
}

function author(): string {
  try {
    return localStorage.getItem(AUTHOR_KEY) ?? "";
  } catch {
    return "";
  }
}

/** Every human decision on this assessment's findings in one place: waivers (active and past), policies, and waivers waiting for migration. */
export function WaiversPanel({ assessmentId, onChanged }: { assessmentId: string; onChanged?: () => void }) {
  const { t, term, formatDate } = useI18n();
  const [suppressions, setSuppressions] = useState<Suppression[] | null>(null);
  const [policies, setPolicies] = useState<SuppressionPolicy[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [showHistory, setShowHistory] = useState(false);
  const [reload, setReload] = useState(0);

  const [rulePattern, setRulePattern] = useState("");
  const [pathGlob, setPathGlob] = useState("");
  const [reason, setReason] = useState("");
  const [days, setDays] = useState("");
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    api.listSuppressions(assessmentId).then(setSuppressions).catch(() => setError(t("error.load")));
    api.listPolicies(assessmentId).then(setPolicies).catch(() => setPolicies([]));
  }, [assessmentId, t]);

  useEffect(() => {
    load();
  }, [load, reload]);

  const grouped = useMemo(() => {
    const out: Record<WaiverState, Suppression[]> = { active: [], expired: [], revoked: [] };
    for (const s of suppressions ?? []) out[waiverState(s)].push(s);
    return out;
  }, [suppressions]);

  async function reopen(s: Suppression) {
    const who = author() || window.prompt(t("triage.author")) || "";
    if (!who) return;
    setError(null);
    try {
      await api.triage(assessmentId, s.findingId, { action: "Reopen", reason: null, author: who }, "en");
      setMessage(t("waivers.reopened"));
      setReload((r) => r + 1);
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function createPolicy(e: FormEvent) {
    e.preventDefault();
    const who = author() || window.prompt(t("triage.author")) || "";
    if (!who) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const expires = days && Number(days) > 0 ? new Date(Date.now() + Number(days) * 86400000).toISOString() : null;
      const result = await api.createPolicy(assessmentId, { rulePattern: rulePattern.trim(), pathGlob: pathGlob.trim() || null, reason: reason.trim(), author: who, expiresAtUtc: expires });
      setMessage(t("triage.policyCreated", { applied: result.appliedToExisting }));
      setRulePattern("");
      setPathGlob("");
      setReason("");
      setDays("");
      setReload((r) => r + 1);
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function removePolicy(p: SuppressionPolicy) {
    if (!window.confirm(t("waivers.confirmPolicyDelete"))) return;
    try {
      await api.deletePolicy(p.id);
      setReload((r) => r + 1);
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  if (!suppressions) return <><Skeleton kind="tile" count={3} /><Skeleton kind="block" /></>;

  const table = (rows: Suppression[], withActions: boolean) => (
    <table>
      <thead>
        <tr>
          <th>{t("waivers.kind")}</th>
          <th>{t("triage.reason")}</th>
          <th>{t("waivers.by")}</th>
          <th>{t("waivers.when")}</th>
          <th>{t("waivers.until")}</th>
          {withActions && <th />}
        </tr>
      </thead>
      <tbody>
        {rows.map((s) => {
          const state = waiverState(s);
          return (
            <tr key={s.id} className={state === "active" ? "" : "triaged"}>
              <td><span className={`pill ${s.kind === "FalsePositive" ? "soft" : "warn"}`}>{term("fstatus", s.kind)}</span></td>
              <td className="small">{s.reason || <span className="muted">—</span>}</td>
              <td className="small">{s.author}</td>
              <td className="small">{formatDate(s.createdAtUtc)}</td>
              <td className="small">
                {state === "revoked" ? (
                  <span className="muted">{t("waivers.revokedBy", { by: s.revokedBy ?? "—", when: formatDate(s.revokedAtUtc) })}</span>
                ) : s.expiresAtUtc ? (
                  <span className={state === "expired" ? "bad" : ""}>{formatDate(s.expiresAtUtc)}</span>
                ) : (
                  <span className="muted">{t("waivers.noExpiry")}</span>
                )}
              </td>
              {withActions && (
                <td>
                  <button type="button" className="button small" onClick={() => void reopen(s)} title={t("waivers.reopenHint")}>{t("triage.reopen")}</button>
                </td>
              )}
            </tr>
          );
        })}
      </tbody>
    </table>
  );

  return (
    <>
      <p className="muted" style={{ margin: "0 0 1rem" }}>{t("waivers.intro")}</p>
      <div className="kpis">
        <Tile value={grouped.active.length} label={t("waivers.active")} tone={grouped.active.length > 0 ? "medium" : "ok"} />
        <Tile value={grouped.expired.length} label={t("waivers.expired")} tone="neutral" />
        <Tile value={grouped.revoked.length} label={t("waivers.revoked")} tone="neutral" />
        <Tile value={policies?.length ?? 0} label={t("findings.policies")} tone="neutral" />
      </div>

      {error && <ErrorBox message={error} />}
      {message && <p className="banner ok">{message}</p>}

      <WaiversCard assessmentId={assessmentId} onMigrated={() => { setReload((r) => r + 1); onChanged?.(); }} />

      <Card title={t("waivers.activeTitle")} subtitle={t("waivers.activeHint")}>
        {grouped.active.length === 0 ? <p className="muted">{t("waivers.noneActive")}</p> : table(grouped.active, true)}
      </Card>

      {(grouped.expired.length > 0 || grouped.revoked.length > 0) && (
        <Card title={t("waivers.historyTitle")} actions={<button type="button" className="button small" onClick={() => setShowHistory(!showHistory)}>{showHistory ? t("waivers.hide") : t("waivers.show", { n: grouped.expired.length + grouped.revoked.length })}</button>}>
          {showHistory ? table([...grouped.expired, ...grouped.revoked].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)), false) : <p className="muted small">{t("waivers.historyHint")}</p>}
        </Card>
      )}

      <Card title={t("findings.policies")} subtitle={t("waivers.policiesHint")}>
        {policies === null ? (
          <Skeleton count={2} />
        ) : policies.length === 0 ? (
          <EmptyState glyph="§" title={t("waivers.noPolicies")} text={t("findings.policiesEmpty")} />
        ) : (
          <table>
            <thead>
              <tr>
                <th>{t("findings.policyRule")}</th>
                <th>{t("findings.policyPath")}</th>
                <th>{t("triage.reason")}</th>
                <th>{t("waivers.until")}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {policies.map((p) => (
                <tr key={p.id}>
                  <td className="mono">
                    {p.rulePattern}
                    {p.assessmentId === null && <span className="tag">{t("findings.policyGlobal")}</span>}
                  </td>
                  <td className="mono">{p.pathGlob ?? "—"}</td>
                  <td className="small">{p.reason} <span className="muted">· {p.author}</span></td>
                  <td className="small">{p.expiresAtUtc ? formatDate(p.expiresAtUtc) : <span className="muted">{t("waivers.noExpiry")}</span>}</td>
                  <td><button type="button" className="button small danger" onClick={() => void removePolicy(p)}>{t("findings.policyDelete")}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <form className="form source-form" onSubmit={createPolicy}>
          <div className="discover-row">
            <label style={{ flex: 1 }}>
              <span>{t("waivers.policyRule")}</span>
              <input className="mono" value={rulePattern} onChange={(e) => setRulePattern(e.target.value)} placeholder="quality.file.large or quality.*" required />
            </label>
            <label style={{ flex: 1 }}>
              <span>{t("waivers.policyPath")}</span>
              <input className="mono" value={pathGlob} onChange={(e) => setPathGlob(e.target.value)} placeholder="src/Legacy/**" />
            </label>
            <label>
              <span>{t("triage.expiresDays")}</span>
              <input type="number" min={1} max={730} value={days} onChange={(e) => setDays(e.target.value)} style={{ maxWidth: "6rem" }} />
            </label>
          </div>
          <label>
            <span>{t("triage.reason")}</span>
            <input value={reason} onChange={(e) => setReason(e.target.value)} required />
          </label>
          <div className="actions">
            <button type="submit" className="button primary" disabled={busy || !rulePattern.trim() || !reason.trim()}>{busy ? t("waivers.creating") : t("waivers.createPolicy")}</button>
            <span className="muted small">{t("waivers.policyNote")}</span>
          </div>
        </form>
      </Card>
    </>
  );
}
