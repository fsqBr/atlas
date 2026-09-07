import { useEffect, useMemo, useState } from "react";
import { api, type AiAllowlist, type CatalogProvider } from "../api";
import { ErrorBox } from "../components";
import { Card, Skeleton } from "../components/ui";
import { useI18n } from "../i18n";
import { KindTag } from "./AiEstatePanel";

const KIND_ORDER = ["external-api", "gateway", "assistant", "observability", "vector-store", "local-runtime", "local-inference"];

/** Catalog providers grouped by kind, in a stable display order. Exported for tests. */
export function groupByKind(providers: CatalogProvider[]): [string, CatalogProvider[]][] {
  const groups = new Map<string, CatalogProvider[]>();
  for (const p of providers) groups.set(p.kind, [...(groups.get(p.kind) ?? []), p]);
  return [...groups.entries()]
    .sort((a, b) => KIND_ORDER.indexOf(a[0]) - KIND_ORDER.indexOf(b[0]))
    .map(([kind, list]) => [kind, [...list].sort((a, b) => a.name.localeCompare(b.name))]);
}

/** The V0.5 policy, editable: which providers the organization approved. Applies to the next run of every assessment. */
export function AllowlistEditor({ catalog, allowlist, onSave, onClear, busy, message, error }: {
  catalog: CatalogProvider[];
  allowlist: AiAllowlist;
  onSave: (ids: string[]) => void;
  onClear: () => void;
  busy: boolean;
  message: string | null;
  error: string | null;
}) {
  const { t, formatDate } = useI18n();
  const [selected, setSelected] = useState<Set<string>>(new Set(allowlist.approvedProviders));
  useEffect(() => setSelected(new Set(allowlist.approvedProviders)), [allowlist]);
  const groups = useMemo(() => groupByKind(catalog), [catalog]);
  const dirty = useMemo(() => {
    const a = [...selected].sort().join(",");
    const b = [...allowlist.approvedProviders].sort().join(",");
    return a !== b;
  }, [selected, allowlist]);

  const toggle = (id: string) => {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelected(next);
  };

  const sourceText = allowlist.source === "tenant"
    ? t("allowlist.sourceTenant", { by: allowlist.updatedBy ?? "—", when: formatDate(allowlist.updatedAtUtc) })
    : allowlist.source === "config"
      ? t("allowlist.sourceConfig")
      : t("allowlist.sourceNone");

  return (
    <Card title={t("allowlist.title")} subtitle={t("allowlist.hint")} actions={<span className={`pill ${allowlist.source === "none" ? "soft" : "ok"}`}>{t(`allowlist.source.${allowlist.source}` as "allowlist.source.none")}</span>}>
      <p className="muted small" style={{ marginTop: 0 }}>{sourceText}</p>
      <div className="check-grid">
        {groups.map(([kind, list]) => (
          <fieldset key={kind} className="check-group">
            <legend><KindTag kind={kind} /> <span className="muted small">{kind === "external-api" ? t("allowlist.judged") : t("allowlist.notJudged")}</span></legend>
            {list.map((p) => (
              <label key={p.id} className="check">
                <input type="checkbox" checked={selected.has(p.id)} onChange={() => toggle(p.id)} />
                <span>{p.name}</span>
                <span className="mono tiny muted">{p.id}</span>
              </label>
            ))}
          </fieldset>
        ))}
      </div>
      {error && <ErrorBox message={error} />}
      {message && <p className="banner ok">{message}</p>}
      <div className="actions" style={{ marginTop: "0.8rem" }}>
        <button type="button" className="button primary" disabled={busy || !dirty || selected.size === 0} onClick={() => onSave([...selected])}>{busy ? t("allowlist.saving") : t("allowlist.save", { n: selected.size })}</button>
        <button type="button" className="button" disabled={busy || allowlist.source !== "tenant"} onClick={onClear}>{t("allowlist.clear")}</button>
        <span className="muted small">{t("allowlist.appliesNextRun")}</span>
      </div>
    </Card>
  );
}

/** Loads the catalog and the saved allowlist; saves through the API. */
export function AllowlistCard({ onChanged }: { onChanged?: () => void }) {
  const { t } = useI18n();
  const [catalog, setCatalog] = useState<CatalogProvider[] | null>(null);
  const [allowlist, setAllowlist] = useState<AiAllowlist | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.catalogProviders().then(setCatalog).catch(() => setCatalog([]));
    api.getAllowlist().then(setAllowlist).catch(() => setError(t("error.load")));
  }, [t]);

  async function save(ids: string[]) {
    setBusy(true);
    setMessage(null);
    setError(null);
    try {
      setAllowlist(await api.saveAllowlist(ids));
      setMessage(t("allowlist.saved"));
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function clear() {
    if (!window.confirm(t("allowlist.confirmClear"))) return;
    setBusy(true);
    setMessage(null);
    setError(null);
    try {
      await api.clearAllowlist();
      setAllowlist(await api.getAllowlist());
      setMessage(t("allowlist.cleared"));
      onChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  if (!catalog || !allowlist) return <Card title={t("allowlist.title")}><Skeleton count={4} /></Card>;
  return <AllowlistEditor catalog={catalog} allowlist={allowlist} onSave={(ids) => void save(ids)} onClear={() => void clear()} busy={busy} message={message} error={error} />;
}
