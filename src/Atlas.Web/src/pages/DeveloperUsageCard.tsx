import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, type AiUsageSummary } from "../api";
import { Card, Tile } from "../components/ui";
import { useI18n } from "../i18n";

const RELEASES = "https://github.com/fsqBr/atlas/releases/latest";

function fmtTokens(n: number): string {
  if (n >= 1_000_000_000) return `${(n / 1_000_000_000).toFixed(2)}B`;
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return String(n);
}

function money(v: number, currency: string, locale = "en"): string {
  return `${currency === "USD" ? "$" : `${currency} `}${v.toLocaleString(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

/** Pure view over the usage summary (testable without the API). */
export function UsageView({ usage }: { usage: AiUsageSummary }) {
  const { t, lang, formatDate } = useI18n();
  const $ = (v: number) => money(v, usage.currency, lang);
  const maxCost = Math.max(...usage.actors.map((a) => a.estimatedCost), 0.01);
  const maxDay = Math.max(...usage.byDay.map((d) => d.tokens), 1);
  const avgPerDev = usage.reportingActors > 0 ? usage.estimatedCost / usage.reportingActors : 0;
  const monthly = usage.days > 0 ? (usage.estimatedCost / usage.days) * 30 : 0;

  return (
    <>
      <div className="tiles">
        <Tile tone="accent" label={t("usage.devs")} value={usage.reportingActors} hint={t("usage.devsHint", { days: usage.days })} />
        <Tile tone="neutral" label={t("usage.tokens")} value={fmtTokens(usage.totalTokens)} hint={t("usage.tokensHint")} />
        <Tile tone="medium" label={t("usage.cost")} value={$(usage.estimatedCost)} hint={t("usage.costHint", { catalog: usage.priceCatalogVersion })} />
        <Tile tone="neutral" label={t("usage.perDev")} value={$(avgPerDev)} hint={t("usage.perDevHint")} />
        <Tile tone="neutral" label={t("usage.runRate")} value={$(monthly)} hint={t("usage.runRateHint")} />
        {usage.unpricedTokens > 0 && <Tile tone="high" label={t("usage.unpriced")} value={fmtTokens(usage.unpricedTokens)} hint={t("usage.unpricedHint")} />}
      </div>

      <h4 className="sub">{t("usage.byDev")}</h4>
      <div className="table-scroll">
        <table className="compact usage-table">
            <thead>
              <tr>
                <th>{t("usage.actor")}</th>
                <th>{t("usage.tools")}</th>
                <th className="num">{t("usage.sessions")}</th>
                <th className="num">{t("usage.requests")}</th>
                <th className="num">{t("usage.tokens")}</th>
                <th>{t("usage.cost")}</th>
                <th>{t("usage.lastReport")}</th>
              </tr>
            </thead>
            <tbody>
              {usage.actors.map((a) => (
                <tr key={a.actor}>
                  <td className="mono">{a.actor.startsWith("anon-") ? <span title={t("usage.anonTitle")}>👤 {a.actor}</span> : a.actor}</td>
                  <td>{a.tools.map((x) => <span key={x} className="kind">{x}</span>)}</td>
                  <td className="num">{a.sessions}</td>
                  <td className="num">{a.requests}</td>
                  <td className="num" title={a.tokens.toLocaleString()}>{fmtTokens(a.tokens)}</td>
                  <td>
                    <div className="cost-bar" aria-label={$(a.estimatedCost)}>
                      <span style={{ width: `${Math.max(2, (a.estimatedCost / maxCost) * 100)}%` }} />
                      <b>{$(a.estimatedCost)}</b>
                      {a.unpricedTokens > 0 && <small className="muted" title={t("usage.unpricedHint")}> +{fmtTokens(a.unpricedTokens)} {t("usage.unpricedShort")}</small>}
                    </div>
                  </td>
                  <td className="muted small">{formatDate(a.lastReportUtc)}</td>
                </tr>
              ))}
            </tbody>
        </table>
      </div>
      <div className="usage-grid">
        <div>
          <h4 className="sub">{t("usage.byModel")}</h4>
          <table className="compact">
            <thead>
              <tr>
                <th>{t("usage.model")}</th>
                <th className="num">{t("usage.tokens")}</th>
                <th className="num">{t("usage.cost")}</th>
                <th className="num">{t("usage.devs")}</th>
              </tr>
            </thead>
            <tbody>
              {usage.byModel.map((m) => (
                <tr key={m.model}>
                  <td className="mono small">{m.model}</td>
                  <td className="num">{fmtTokens(m.tokens)}</td>
                  <td className="num">{m.estimatedCost === null ? <span className="pill warn">{t("usage.unpricedShort")}</span> : $(m.estimatedCost)}</td>
                  <td className="num">{m.actors}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div>
          <h4 className="sub">{t("usage.byDay")}</h4>
          <div className="day-bars" role="img" aria-label={t("usage.byDay")}>
            {usage.byDay.map((d) => (
              <span key={d.period} className="day-bar" title={`${d.period} · ${fmtTokens(d.tokens)} · ${$(d.estimatedCost)} · ${d.actors} ${t("usage.devsShort")}`}>
                <i style={{ height: `${Math.max(3, (d.tokens / maxDay) * 100)}%` }} />
              </span>
            ))}
          </div>
        </div>
      </div>
      <p className="muted small">{t("usage.note", { catalog: usage.priceCatalogVersion })}</p>
    </>
  );
}

export function CliGuide({ server }: { server: string }) {
  const { t } = useI18n();
  const cmd = useMemo(() => `atlas-agent --server ${server} --token <ATLAS_TOKEN> --days 7`, [server]);
  return (
    <details className="more cli-guide">
      <summary>{t("usage.cli.title")}</summary>
      <p>{t("usage.cli.intro")}</p>
      <ol className="guide-steps">
        <li>
          {t("usage.cli.s1")} <a href={RELEASES} target="_blank" rel="noreferrer">{t("usage.cli.download")} ↗</a>
        </li>
        <li>
          {t("usage.cli.s2")} <Link to="/settings/tokens">{t("nav.tokens")} →</Link>
        </li>
        <li>
          {t("usage.cli.s3")}
          <pre className="cmd"><code>{cmd} --dry-run</code></pre>
        </li>
        <li>
          {t("usage.cli.s4")}
          <pre className="cmd"><code>{cmd}</code></pre>
        </li>
        <li>
          {t("usage.cli.s5")}
          <pre className="cmd"><code>{`atlas-agent install --server ${server} --token <ATLAS_TOKEN> --every 12`}</code></pre>
          <span className="small muted">{t("usage.cli.s5hint")}</span>
        </li>
      </ol>
      <div className="callout info">
        <strong>{t("usage.cli.privacyTitle")}</strong> {t("usage.cli.privacy")} <code>--anonymous</code> {t("usage.cli.anon")}
      </div>
    </details>
  );
}

export function DeveloperUsageCard() {
  const { t } = useI18n();
  const [days, setDays] = useState(30);
  const [usage, setUsage] = useState<AiUsageSummary | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setUsage(undefined);
    api.getUsageSummary(days).then(setUsage).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [days]);

  const server = typeof window !== "undefined" ? window.location.origin : "https://atlas.example.com";

  return (
    <Card
      title={t("usage.title")}
      subtitle={t("usage.hint")}
      actions={
        <label className="small muted row" style={{ gap: "0.3rem" }}>
          {t("usage.window")}
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            {[7, 30, 90].map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        </label>
      }
    >
      {error ? (
        <p className="banner bad">{error}</p>
      ) : usage === undefined ? (
        <p className="muted">…</p>
      ) : usage === null ? (
        <div className="callout">
          <strong>{t("usage.empty.title")}</strong> {t("usage.empty.text")}
        </div>
      ) : (
        <UsageView usage={usage} />
      )}
      <CliGuide server={server} />
    </Card>
  );
}
