import { useEffect, useState } from "react";
import { api, type Reconciliation } from "../api";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";

function money(v: number, lang: string): string {
  return `$${v.toLocaleString(lang, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

/** Pure view: estimated vs billed per provider, with the ratio and whether forecasts use it. */
export function ReconciliationView({ data }: { data: Reconciliation }) {
  const { t, lang } = useI18n();
  if (data.providers.length === 0) return <p className="muted">{t("recon.none")}</p>;
  return (
    <>
      {data.blendedRatio !== null ? (
        <div className="callout ok"><strong>{t("recon.calibrated", { ratio: data.blendedRatio.toLocaleString(lang, { maximumFractionDigits: 2 }) })}</strong> {t("recon.calibratedHint")}</div>
      ) : (
        <div className="callout info">{t("recon.notYet")}</div>
      )}
      <div className="table-scroll">
        <table className="compact">
          <thead>
            <tr>
              <th>{t("breakdown.provider")}</th>
              <th className="num">{t("recon.estimated")}</th>
              <th className="num">{t("recon.reported")}</th>
              <th className="num">{t("recon.overlap")}</th>
              <th className="num">{t("recon.ratio")}</th>
              <th>{t("recon.note")}</th>
            </tr>
          </thead>
          <tbody>
            {data.providers.map((p) => (
              <tr key={p.provider}>
                <td className="mono">{p.provider}</td>
                <td className="num">{money(p.estimated, lang)}</td>
                <td className="num">{money(p.reported, lang)}</td>
                <td className="num" title={`${p.daysEstimatedOnly} ${t("recon.estOnly")}, ${p.daysReportedOnly} ${t("recon.repOnly")}`}>{p.daysWithBoth}</td>
                <td className="num">{p.ratio === null ? "—" : <span className={`pill ${p.ratioUsable ? (p.ratio > 1.5 || p.ratio < 0.67 ? "warn" : "ok") : "soft"}`}>×{p.ratio.toLocaleString(lang, { maximumFractionDigits: 2 })}</span>}</td>
                <td className="small muted">{p.note}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted small">{t("recon.method")}</p>
    </>
  );
}

export function ReconciliationCard() {
  const { t } = useI18n();
  const [data, setData] = useState<Reconciliation | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api.getReconciliation(30).then(setData).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);
  return (
    <Card title={t("recon.title")} subtitle={t("recon.hint")}>
      {error ? <p className="banner bad">{error}</p> : data === null ? <p className="muted">…</p> : <ReconciliationView data={data} />}
    </Card>
  );
}
