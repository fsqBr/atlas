import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type ModelPrice, type PriceCatalog } from "../api";
import { ErrorBox } from "../components";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";

export type PriceDraft = { pattern: string; input: string; output: string; cacheRead: string; cacheWrite: string; note: string };

export const emptyDraft: PriceDraft = { pattern: "", input: "", output: "", cacheRead: "", cacheWrite: "", note: "" };

export function draftFrom(p: ModelPrice): PriceDraft {
  return { pattern: p.pattern, input: String(p.input), output: String(p.output), cacheRead: p.cacheRead === null ? "" : String(p.cacheRead), cacheWrite: p.cacheWrite === null ? "" : String(p.cacheWrite), note: p.note ?? "" };
}

function num(v: string): number | null {
  if (v.trim() === "") return null;
  const n = Number(v.replace(",", "."));
  return Number.isFinite(n) ? n : NaN;
}

function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(n);
}

/** Pure view: the effective price list with tenant lines first, plus the unpriced models seen in reports. */
export function ModelPricesView({ catalog, onPick, onRemove }: { catalog: PriceCatalog; onPick: (draft: PriceDraft) => void; onRemove: (id: string) => void }) {
  const { t, lang } = useI18n();
  const money = (v: number | null) => (v === null ? "—" : v.toLocaleString(lang, { minimumFractionDigits: 2, maximumFractionDigits: 4 }));
  return (
    <>
      {catalog.unpriced.length > 0 && (
        <div className="callout warn">
          <strong>{t("prices.unpricedSeen", { count: catalog.unpriced.length })}</strong> {t("prices.unpricedSeenHint")}
          <div className="chips">
            {catalog.unpriced.map((u) => (
              <button key={u.model} type="button" className="chip" onClick={() => onPick({ ...emptyDraft, pattern: `^${u.model.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$` })} title={t("prices.setPriceFor", { model: u.model })}>
                <code>{u.model}</code> <small>{fmtTokens(u.tokens)} · {u.actors} {t("usage.devsShort")}</small>
              </button>
            ))}
          </div>
        </div>
      )}
      {(() => {
        const tenant = catalog.prices.filter((p) => p.source === "tenant");
        const builtin = catalog.prices.filter((p) => p.source !== "tenant");
        const table = (rows: ModelPrice[]) => (
          <div className="table-scroll">
            <table className="compact prices-table">
              <thead>
                <tr>
                  <th>{t("prices.pattern")}</th>
                  <th className="num">{t("prices.input")}</th>
                  <th className="num">{t("prices.output")}</th>
                  <th className="num">{t("prices.cacheRead")}</th>
                  <th className="num">{t("prices.cacheWrite")}</th>
                  <th>{t("prices.source")}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {rows.map((p) => (
                  <tr key={p.id ?? `b-${p.pattern}`} className={p.source === "tenant" ? "tenant-row" : undefined}>
                    <td className="mono small" title={p.note ?? undefined}>{p.pattern}{p.note && <span className="muted"> · {p.note}</span>}</td>
                    <td className="num">{money(p.input)}</td>
                    <td className="num">{money(p.output)}</td>
                    <td className="num">{money(p.cacheRead)}</td>
                    <td className="num">{money(p.cacheWrite)}</td>
                    <td>
                      <span className={`pill ${p.source === "tenant" ? "accent" : "soft"}`}>{t(`prices.source.${p.source}` as "prices.source.tenant")}</span>
                      {p.source === "tenant" && p.updatedBy && <small className="muted"> {p.updatedBy}</small>}
                    </td>
                    <td>
                      <div className="actions">
                        <button type="button" className="button small" onClick={() => onPick(draftFrom(p))}>{p.source === "tenant" ? t("prices.edit") : t("prices.override")}</button>
                        {p.source === "tenant" && p.id && <button type="button" className="button small danger" onClick={() => onRemove(p.id!)}>{t("prices.remove")}</button>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        );
        return (
          <>
            <h4 className="sub">{t("prices.yours", { count: tenant.length })}</h4>
            {tenant.length > 0 ? table(tenant) : <p className="muted small">{t("prices.noneYours")}</p>}
            <details className="more">
              <summary>{t("prices.builtinList", { count: builtin.length, version: catalog.baseVersion })}</summary>
              {table(builtin)}
            </details>
          </>
        );
      })()}
      <p className="muted small">{t("prices.note", { version: catalog.version, currency: catalog.currency })}</p>
    </>
  );
}

export function ModelPricesCard({ onChanged }: { onChanged?: () => void }) {
  const { t } = useI18n();
  const [catalog, setCatalog] = useState<PriceCatalog | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<PriceDraft>(emptyDraft);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const load = useCallback(() => {
    api.getUsagePrices().then(setCatalog).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  useEffect(load, [load]);

  async function save(e: FormEvent) {
    e.preventDefault();
    setFormError(null);
    setMessage(null);
    const input = num(draft.input);
    const output = num(draft.output);
    const cacheRead = num(draft.cacheRead);
    const cacheWrite = num(draft.cacheWrite);
    if (!draft.pattern.trim() || input === null || output === null || [input, output, cacheRead, cacheWrite].some((v) => v !== null && (Number.isNaN(v) || v < 0))) {
      setFormError(t("prices.invalid"));
      return;
    }
    setBusy(true);
    try {
      await api.upsertModelPrice({ pattern: draft.pattern.trim(), input, output, cacheRead, cacheWrite, note: draft.note.trim() || null });
      setMessage(t("prices.saved"));
      setDraft(emptyDraft);
      load();
      onChanged?.();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function remove(id: string) {
    if (!window.confirm(t("prices.confirmRemove"))) return;
    try {
      await api.deleteModelPrice(id);
      load();
      onChanged?.();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    }
  }

  const field = (k: keyof PriceDraft) => (e: { target: { value: string } }) => setDraft((d) => ({ ...d, [k]: e.target.value }));

  return (
    <Card title={t("prices.title")} subtitle={t("prices.hint")}>
      {error ? <ErrorBox message={error} /> : catalog === null ? <p className="muted">…</p> : <ModelPricesView catalog={catalog} onPick={(d) => { setDraft(d); setMessage(null); }} onRemove={(id) => void remove(id)} />}

      <form className="form price-form" onSubmit={save}>
        <h4 className="sub">{draft.pattern ? t("prices.editing", { pattern: draft.pattern }) : t("prices.add")}</h4>
        <div className="price-grid">
          <label className="wide">
            <span>{t("prices.pattern")}</span>
            <input className="mono" value={draft.pattern} onChange={field("pattern")} placeholder="^claude-opus-5" required />
            <small className="muted">{t("prices.patternHint")}</small>
          </label>
          <label>
            <span>{t("prices.input")}</span>
            <input inputMode="decimal" value={draft.input} onChange={field("input")} placeholder="5.00" required />
          </label>
          <label>
            <span>{t("prices.output")}</span>
            <input inputMode="decimal" value={draft.output} onChange={field("output")} placeholder="25.00" required />
          </label>
          <label>
            <span>{t("prices.cacheRead")}</span>
            <input inputMode="decimal" value={draft.cacheRead} onChange={field("cacheRead")} placeholder="0.50" />
          </label>
          <label>
            <span>{t("prices.cacheWrite")}</span>
            <input inputMode="decimal" value={draft.cacheWrite} onChange={field("cacheWrite")} placeholder="6.25" />
          </label>
          <label className="wide">
            <span>{t("prices.noteLabel")}</span>
            <input value={draft.note} onChange={field("note")} placeholder={t("prices.notePlaceholder")} />
          </label>
        </div>
        {formError && <ErrorBox message={formError} />}
        {message && <p className="banner ok">{message}</p>}
        <div className="actions">
          <button type="submit" className="button primary" disabled={busy}>{busy ? t("prices.saving") : t("prices.save")}</button>
          {draft.pattern && <button type="button" className="button" onClick={() => setDraft(emptyDraft)}>{t("prices.clear")}</button>}
          <span className="muted small">{t("prices.unit")} · {t("prices.adminOnly")}</span>
        </div>
      </form>
    </Card>
  );
}
