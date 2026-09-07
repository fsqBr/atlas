import { useCallback, useEffect, useState } from "react";
import { api, type MigratableWaiver } from "../api";
import { SeverityChip } from "../components";
import { useI18n } from "../i18n";

const AUTHOR_KEY = "atlas.triage.author";

/**
 * Waivers orphaned by a rule major bump: the new finding is Open, the superseded one still
 * carries the waiver. Migration is an explicit, audited human decision — pick what still applies.
 * Renders nothing when there is nothing to migrate.
 */
export function WaiversCard({ assessmentId, onMigrated }: { assessmentId: string; onMigrated?: () => void }) {
  const { t, formatDate } = useI18n();
  const [items, setItems] = useState<MigratableWaiver[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(() => {
    api.listMigratableWaivers(assessmentId).then((list) => {
      setItems(list);
      setSelected(new Set());
    }).catch(() => setItems([]));
  }, [assessmentId]);

  useEffect(() => {
    load();
  }, [load]);

  if (!items || items.length === 0) return null;

  async function migrate(all: boolean) {
    let author = "";
    try {
      author = localStorage.getItem(AUTHOR_KEY) ?? "";
    } catch {
      /* storage unavailable */
    }
    author = author || window.prompt(t("triage.author")) || "";
    if (!author) return;
    try {
      localStorage.setItem(AUTHOR_KEY, author);
    } catch {
      /* ignore */
    }
    setBusy(true);
    setMessage(null);
    try {
      const result = await api.migrateWaivers(assessmentId, all ? null : [...selected], author);
      setMessage(t("waivers.done", { migrated: result.migrated, skipped: result.skipped }));
      load();
      onMigrated?.();
    } catch (err) {
      setMessage(`${t("triage.error")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setBusy(false);
    }
  }

  const toggle = (id: string) => {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelected(next);
  };

  return (
    <section className="card waivers">
      <div className="card-head">
        <h2>{t("waivers.title")} <span className="tag">{items.length}</span></h2>
        <div className="actions">
          <button type="button" className="button small" disabled={busy || selected.size === 0} onClick={() => void migrate(false)}>{t("waivers.migrateSelected", { n: selected.size })}</button>
          <button type="button" className="button small primary" disabled={busy} onClick={() => void migrate(true)}>{t("waivers.migrateAll")}</button>
        </div>
        <p className="sub">{t("waivers.hint")}</p>
      </div>
      {message && <p className="banner small">{message}</p>}
      <table>
        <thead>
          <tr>
            <th />
            <th>{t("findings.severity")}</th>
            <th>{t("waivers.finding")}</th>
            <th>{t("waivers.waiver")}</th>
          </tr>
        </thead>
        <tbody>
          {items.map((w) => (
            <tr key={w.findingId}>
              <td><input type="checkbox" checked={selected.has(w.findingId)} onChange={() => toggle(w.findingId)} aria-label={w.title} /></td>
              <td><SeverityChip severity={w.severity} /></td>
              <td>
                <div className="strong">{w.title}</div>
                <div className="mono small muted">{w.ruleId} · ← {w.predecessorFingerprint.slice(0, 12)}</div>
              </td>
              <td className="small">
                <strong>{w.waiverKind}</strong> · {w.waiverAuthor} · {w.waiverReason}
                <div className="muted">{formatDate(w.waiverCreatedAtUtc)}{w.waiverExpiresAtUtc ? ` · ${t("waivers.expires", { when: formatDate(w.waiverExpiresAtUtc) })}` : ""}</div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
