import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, type AiUsageSummary, type LiveUsage, type UsageForecast } from "../api";
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

function relative(iso: string, now: Date, t: (k: "usage.live.justNow" | "usage.live.minAgo" | "usage.live.hAgo", p?: Record<string, string | number>) => string): string {
  const min = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 60000));
  if (min < 1) return t("usage.live.justNow");
  if (min < 60) return t("usage.live.minAgo", { n: min });
  return t("usage.live.hAgo", { n: Math.round(min / 60) });
}

/** Today as it happens: who reported in the last N minutes, running totals, tokens per hour, the intraday curve. */
export function LiveView({ live, now = new Date() }: { live: LiveUsage; now?: Date }) {
  const { t, lang } = useI18n();
  const $ = (v: number) => money(v, "USD", lang);
  const maxPoint = Math.max(...live.curve.map((p) => p.tokens), 1);
  return (
    <div className="live" data-testid="live-view">
      <div className="live-head">
        <span className="live-dot" aria-hidden="true" />
        <strong>{t("usage.live.title")}</strong>
        <span className="muted small">{t("usage.live.hint", { minutes: live.activeMinutes })} · {t("usage.live.asOf")} {new Date(live.asOfUtc).toLocaleTimeString(lang, { hour: "2-digit", minute: "2-digit" })}</span>
      </div>
      <div className="tiles live-tiles">
        <Tile tone={live.activeActors > 0 ? "ok" : "neutral"} label={t("usage.live.active")} value={live.activeActors} hint={t("usage.live.activeHint", { total: live.reportingActorsToday })} />
        <Tile tone="neutral" label={t("usage.live.today")} value={fmtTokens(live.tokensToday)} hint={t("usage.live.todayHint")} />
        <Tile tone="medium" label={t("usage.live.costToday")} value={$(live.estimatedCostToday)} hint={t("usage.live.costTodayHint")} />
        <Tile tone="accent" label={t("usage.live.perHour")} value={fmtTokens(live.tokensLastHour)} hint={t("usage.live.perHourHint")} />
      </div>
      <div className="usage-grid">
        <div className="table-scroll">
          <table className="compact usage-table">
            <thead>
              <tr>
                <th>{t("usage.actor")}</th>
                <th>{t("usage.live.status")}</th>
                <th className="num">{t("usage.live.tokensToday")}</th>
                <th className="num">{t("usage.live.lastHour")}</th>
                <th className="num">{t("usage.cost")}</th>
                <th>{t("usage.lastReport")}</th>
              </tr>
            </thead>
            <tbody>
              {live.actors.map((a) => (
                <tr key={a.actor}>
                  <td className="mono">{a.actor}</td>
                  <td>{a.activeNow ? <span className="pill ok">{t("usage.live.activeNow")}</span> : <span className="pill soft">{t("usage.live.idle")}</span>}</td>
                  <td className="num">{fmtTokens(a.tokensToday)}</td>
                  <td className="num">{a.tokensLastHour > 0 ? `+${fmtTokens(a.tokensLastHour)}` : "—"}</td>
                  <td className="num">{$(a.estimatedCostToday)}</td>
                  <td className="muted small">{relative(a.lastReportUtc, now, t)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div>
          <h4 className="sub">{t("usage.live.curve")}</h4>
          <svg className="live-curve" viewBox="0 0 400 100" preserveAspectRatio="none" role="img" aria-label={t("usage.live.curve")}>
            {live.curve.length > 1 && (
              <polyline
                fill="none"
                stroke="var(--accent)"
                strokeWidth="2"
                points={live.curve.map((p, i) => `${(i / (live.curve.length - 1)) * 400},${100 - (p.tokens / maxPoint) * 92}`).join(" ")}
              />
            )}
            {live.curve.length > 1 && (
              <polygon
                fill="var(--accent)"
                fillOpacity="0.12"
                points={`0,100 ${live.curve.map((p, i) => `${(i / (live.curve.length - 1)) * 400},${100 - (p.tokens / maxPoint) * 92}`).join(" ")} 400,100`}
              />
            )}
          </svg>
          <div className="live-axis muted small"><span>00:00 UTC</span><span>{new Date(live.asOfUtc).toLocaleTimeString(lang, { hour: "2-digit", minute: "2-digit", timeZone: "UTC" })} UTC</span></div>
        </div>
      </div>
    </div>
  );
}

/** Month-to-date and straight-line projections; explained in place so nobody mistakes them for a bill. */
export function ForecastTiles({ forecast, currency, lang }: { forecast: UsageForecast; currency: string; lang: string }) {
  const { t } = useI18n();
  const $ = (v: number) => money(v, currency, lang);
  const trend = forecast.trendPercent;
  return (
    <div className="tiles forecast-tiles">
      <Tile tone="neutral" label={t("usage.fc.mtd")} value={$(forecast.monthToDateCost)} hint={t("usage.fc.mtdHint", { d: forecast.daysElapsedInMonth, n: forecast.daysInMonth })} />
      <Tile tone="medium" label={t("usage.fc.month")} value={$(forecast.projectedMonthSeasonal || forecast.projectedMonthCost)} hint={forecast.projectedMonthLow || forecast.projectedMonthHigh ? t("usage.fc.bandHint", { low: $(forecast.projectedMonthLow), high: $(forecast.projectedMonthHigh) }) : t("usage.fc.monthHint")} />
      {forecast.projectedMonthCalibrated !== null && forecast.projectedMonthCalibrated !== undefined && (
        <Tile tone="ok" label={t("usage.fc.calibrated")} value={$(forecast.projectedMonthCalibrated)} hint={t("usage.fc.calibratedHint", { ratio: (forecast.calibrationRatio ?? 1).toLocaleString(lang, { maximumFractionDigits: 2 }) })} />
      )}
      <Tile tone="accent" label={t("usage.fc.next30")} value={$(forecast.projectedNext30Cost)} hint={t("usage.fc.next30Hint", { d: $(forecast.dailyAverage7) })} />
      <Tile
        tone={trend === null ? "neutral" : trend > 10 ? "high" : trend < -10 ? "ok" : "neutral"}
        label={t("usage.fc.trend")}
        value={trend === null ? "—" : `${trend > 0 ? "+" : ""}${trend.toLocaleString(lang, { maximumFractionDigits: 0 })}%`}
        hint={trend === null ? t("usage.fc.trendNone") : t("usage.fc.trendHint", { a: $(forecast.dailyAverage7), b: $(forecast.dailyAverage30) })}
      />
      {forecast.unpricedTokensMonthToDate > 0 && <Tile tone="high" label={t("usage.fc.unpriced")} value={fmtTokens(forecast.unpricedTokensMonthToDate)} hint={t("usage.fc.unpricedHint")} />}
    </div>
  );
}

/** Burn-up: cumulative estimated spend per day of the month against a straight line to the projection (and the budget, when given). */
export function BurnUp({ forecast, budget, lang }: { forecast: UsageForecast; budget?: number | null; lang: string }) {
  const { t } = useI18n();
  const days = forecast.daysInMonth;
  const series = forecast.cumulativeByDay ?? [];
  const target = forecast.projectedMonthSeasonal || forecast.projectedMonthCost;
  const max = Math.max(target, budget ?? 0, ...series, 0.01);
  const x = (day: number) => ((day - 1) / Math.max(1, days - 1)) * 400;
  const y = (v: number) => 100 - (v / max) * 92;
  const $ = (v: number) => `$${v.toLocaleString(lang, { maximumFractionDigits: 0 })}`;
  return (
    <div className="burnup" data-testid="burnup">
      <svg viewBox="0 0 400 100" preserveAspectRatio="none" role="img" aria-label={t("usage.fc.burnup")}>
        {budget ? <line x1="0" y1={y(budget)} x2="400" y2={y(budget)} stroke="var(--crit)" strokeDasharray="4 3" strokeWidth="1.5" /> : null}
        <line x1={x(series.length || 1)} y1={y(series[series.length - 1] ?? 0)} x2="400" y2={y(target)} stroke="var(--soft)" strokeDasharray="3 3" strokeWidth="1.5" />
        {series.length > 1 && <polyline fill="none" stroke="var(--accent)" strokeWidth="2.5" points={series.map((v, i) => `${x(i + 1)},${y(v)}`).join(" ")} />}
      </svg>
      <div className="live-axis muted small">
        <span>1</span>
        <span>{t("usage.fc.burnupLegend", { proj: $(target) })}{budget ? ` · ${t("usage.fc.burnupBudget", { b: $(budget) })}` : ""}</span>
        <span>{days}</span>
      </div>
    </div>
  );
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

      <h4 className="sub">{t("usage.fc.title")}</h4>
      <ForecastTiles forecast={usage.forecast} currency={usage.currency} lang={lang} />
      <BurnUp forecast={usage.forecast} lang={lang} />

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
                <th className="num">{t("usage.fc.mtdShort")}</th>
                <th className="num">{t("usage.fc.monthShort")}</th>
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
                  <td className="num">{$(a.forecast.monthToDateCost)}</td>
                  <td className="num" title={t("usage.fc.monthHint")}>{$(a.forecast.projectedMonthCost)}</td>
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
          <pre className="cmd"><code>{`atlas-agent install --server ${server} --token <ATLAS_TOKEN> --every 12h`}</code></pre>
          <span className="small muted">{t("usage.cli.s5hint")}</span>
        </li>
        <li>
          {t("usage.cli.s6")}
          <pre className="cmd"><code>{`atlas-agent install --server ${server} --token <ATLAS_TOKEN> --every 5m\natlas-agent watch --server ${server} --token <ATLAS_TOKEN>`}</code></pre>
          <span className="small muted">{t("usage.cli.s6hint")}</span>
        </li>
      </ol>
      <p className="small muted">{t("usage.cli.tools")}</p>
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
  const [live, setLive] = useState<LiveUsage | null>(null);
  const [auto, setAuto] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setUsage(undefined);
    api.getUsageSummary(days).then(setUsage).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [days]);

  // The live view follows the agent's reports (install --every 5m or watch); one poll a minute is plenty.
  useEffect(() => {
    let stop = false;
    const tick = () => {
      api.getUsageLive(15).then((l) => { if (!stop) setLive(l); }).catch(() => {});
      if (auto) api.getUsageSummary(days).then((u) => { if (!stop) setUsage(u); }).catch(() => {});
    };
    tick();
    const id = auto ? window.setInterval(tick, 60_000) : undefined;
    return () => { stop = true; if (id) window.clearInterval(id); };
  }, [auto, days]);

  const server = typeof window !== "undefined" ? window.location.origin : "https://atlas.example.com";

  return (
    <Card
      title={t("usage.title")}
      subtitle={t("usage.hint")}
      actions={
        <span className="row">
          <label className="small muted row" style={{ gap: "0.3rem" }}>
            <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> {t("usage.live.auto")}
          </label>
          <label className="small muted row" style={{ gap: "0.3rem" }}>
            {t("usage.window")}
            <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
              {[7, 30, 90].map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
          </label>
          <a className="button small" href={api.usageExportUrl(days)} title={t("usage.exportCsvHint")}>⬇ {t("usage.exportCsv")}</a>
        </span>
      }
    >
      {live && <LiveView live={live} />}
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
