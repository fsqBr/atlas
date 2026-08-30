import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, type Calibration, type Portfolio } from "../api";
import { ErrorBox, RiskBadge, ScoreBadge, SeverityChip, StatusChip } from "../components";
import { Donut, HBars, Legend, RangeBars, severityColor, useTokens } from "../components/charts";
import { PageHeader, Skeleton } from "../components/ui";
import { useI18n } from "../i18n";

const RISKS = ["Critical", "High", "Medium", "Low"] as const;

export function PortfolioPage() {
  const { t, term, lang, formatNumber, formatDate } = useI18n();
  const tk = useTokens();
  const [data, setData] = useState<Portfolio | null>(null);
  const [calibration, setCalibration] = useState<Calibration | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getPortfolio(lang)
      .then(setData)
      .catch(() => setError(t("error.load")));
    api.getCalibration(lang).then(setCalibration).catch(() => setCalibration(null));
  }, [lang, t]);

  if (error) return <ErrorBox message={error} />;
  if (!data) {
    return (
      <>
        <PageHeader title={t("portfolio.title")} />
        <Skeleton kind="tile" count={6} />
        <Skeleton kind="block" />
      </>
    );
  }

  const legacyShare = data.projects === 0 ? 0 : Math.round((data.legacyProjects / data.projects) * 100);

  return (
    <>
      <PageHeader title={t("portfolio.title")} subtitle={t("portfolio.subtitle", { assessments: data.assessments, assessed: data.assessed })} />

      <div className="tiles">
        <div className="tile">
          <span className="tile-v">{data.averageScore === null ? "—" : data.averageScore}</span>
          <span className="tile-l">{t("portfolio.avgScore")}</span>
        </div>
        <div className="tile">
          <span className="tile-v">{formatNumber(data.openFindings)}</span>
          <span className="tile-l">{t("portfolio.openFindings")}</span>
        </div>
        <div className="tile">
          <span className="tile-v">{formatNumber(data.openBySeverity.Critical + data.openBySeverity.High)}</span>
          <span className="tile-l">{t("portfolio.criticalHigh")}</span>
        </div>
        <div className="tile">
          <span className="tile-v">{formatNumber(data.lines)}</span>
          <span className="tile-l">{t("portfolio.lines")}</span>
        </div>
        <div className="tile">
          <span className="tile-v">{formatNumber(data.projects)}</span>
          <span className="tile-l">{t("portfolio.projects")}</span>
        </div>
        <div className="tile">
          <span className="tile-v">{legacyShare}%</span>
          <span className="tile-l">{t("portfolio.legacyShare", { legacy: data.legacyProjects })}</span>
        </div>
      </div>

      <div className="compare-grid">
        <section className="card">
          <h2>{t("portfolio.riskDistribution")}</h2>
          <Donut
            height="h-sm"
            data={RISKS.map((r) => ({ key: r, name: term("risk", r), value: data.byRisk[r] ?? 0 }))}
            colors={(k) => severityColor(tk, k)}
            centerLabel={t("dash.assessments")}
            emptyText={t("dash.noneAssessed")}
          />
          <Legend items={RISKS.map((r) => ({ label: term("risk", r), color: severityColor(tk, r), value: data.byRisk[r] ?? 0 }))} />
          <p className="muted small">
            {(["Critical", "High", "Medium", "Low", "Informational"] as const).map((s) => (
              <span key={s} style={{ marginRight: "0.6rem" }}>
                <SeverityChip severity={s} /> {formatNumber(data.openBySeverity[s])}
              </span>
            ))}
          </p>
        </section>

        <section className="card">
          <h2>{t("portfolio.frameworks")}</h2>
          <HBars
            height="h-md"
            labelWidth={110}
            data={data.frameworks.slice(0, 10).map((f) => ({
              name: f.framework,
              value: f.count,
              color: f.framework === "unknown" ? tk.unknown : f.legacy ? tk.legacy : tk.modern,
              hint: f.framework === "unknown" ? t("portfolio.unknown") : f.legacy ? t("portfolio.legacy") : t("portfolio.modern"),
            }))}
            emptyText={t("mod.none")}
          />
          <Legend items={[{ label: t("portfolio.legacy"), color: tk.legacy, value: data.legacyProjects }, { label: t("portfolio.modern"), color: tk.modern, value: data.modernProjects }, { label: t("portfolio.unknown"), color: tk.unknown, value: data.unknownProjects }]} />
        </section>
      </div>

      <section className="card">
        <h2>{t("portfolio.topRules")}</h2>
        {data.topRules.length === 0 ? (
          <p className="muted">{t("portfolio.noFindings")}</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>{t("findings.severity")}</th>
                <th>{t("findings.finding")}</th>
                <th className="num">{t("portfolio.occurrences")}</th>
                <th className="num">{t("portfolio.assessments")}</th>
              </tr>
            </thead>
            <tbody>
              {data.topRules.map((r) => (
                <tr key={r.ruleId}>
                  <td>
                    <SeverityChip severity={r.maxSeverity} />
                  </td>
                  <td>
                    <div className="strong">{r.title}</div>
                    <div className="mono small muted">
                      {r.ruleId} · {term("cat", r.category)}
                    </div>
                  </td>
                  <td className="num">{formatNumber(r.count)}</td>
                  <td className="num">{r.assessments}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {calibration && (
        <section className="card">
          <h2>{t("portfolio.calibration")}</h2>
          <p className="muted small">
            {t("portfolio.calibrationPoints", { points: calibration.points, median: calibration.medianRatio ?? "—" })} · {calibration.recommendationText}
          </p>
          {calibration.items.length > 0 && (
            <table>
              <tbody>
                {calibration.items.map((i) => (
                  <tr key={i.assessmentId}>
                    <td>
                      <Link to={`/assessments/${i.assessmentId}`}>{i.assessmentName}</Link> <span className="muted small">{i.strategyName}</span>
                    </td>
                    <td className="num">{formatNumber(i.estimatedLikelyHours)} h</td>
                    <td className="num">{formatNumber(i.actualHours)} h</td>
                    <td className={`num ${i.ratio > 1.25 ? "risk-High" : i.ratio < 0.8 ? "risk-Medium" : "risk-Low"}`}>×{i.ratio}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      )}

      {data.benchmark.length > 0 && (
        <section className="card">
          <h2>{t("portfolio.benchmark")}</h2>
          <p className="muted small">{t("portfolio.benchmarkHint")} · {t("portfolio.rangeHint")}</p>
          <RangeBars rows={data.benchmark.map((b) => ({ name: b.name === "Overall" ? t("list.score") : term("dim", b.name), p25: b.p25, p50: b.p50, p75: b.p75, best: b.best, worst: b.worst }))} />
          <details className="more">
          <summary>{t("portfolio.benchmarkTable")}</summary>
          <table>
            <thead>
              <tr>
                <th>{t("portfolio.dimension")}</th>
                <th className="num">{t("portfolio.count")}</th>
                <th className="num">P25</th>
                <th className="num">P50</th>
                <th className="num">P75</th>
                <th className="num">{t("portfolio.best")}</th>
                <th className="num">{t("portfolio.worst")}</th>
              </tr>
            </thead>
            <tbody>
              {data.benchmark.map((b) => (
                <tr key={b.name} className={b.name === "Overall" ? "strong" : ""}>
                  <td>{b.name === "Overall" ? t("list.score") : b.name}</td>
                  <td className="num">{b.count}</td>
                  <td className="num">{b.p25}</td>
                  <td className="num">{b.p50}</td>
                  <td className="num">{b.p75}</td>
                  <td className="num">{b.best}</td>
                  <td className="num">{b.worst}</td>
                </tr>
              ))}
            </tbody>
          </table>
          </details>
          {Object.entries(data.targets).some(([k, v]) => k !== "None" && v > 0) && (
            <p className="small">
              <strong>{t("portfolio.targets")}:</strong>{" "}
              {["Met", "OnTrack", "AtRisk", "Missed"]
                .filter((k) => (data.targets[k] ?? 0) > 0)
                .map((k) => `${t(`target.${k}` as "target.Met")} ${data.targets[k]}`)
                .join(" · ")}
            </p>
          )}
        </section>
      )}

      <section className="card">
        <h2>{t("portfolio.assessmentsTitle")}</h2>
        <table>
          <thead>
            <tr>
              <th>{t("list.name")}</th>
              <th>{t("list.status")}</th>
              <th className="num">{t("list.score")}</th>
              <th className="num" title={t("portfolio.benchmarkHint")}>{t("portfolio.percentile")}</th>
              <th>{t("portfolio.risk")}</th>
              <th>{t("portfolio.target")}</th>
              <th className="num">{t("list.findings")}</th>
              <th className="num">{t("portfolio.lines")}</th>
              <th className="num">{t("portfolio.projects")}</th>
              <th className="num">{t("portfolio.legacyProjects")}</th>
              <th>{t("detail.completed")}</th>
            </tr>
          </thead>
          <tbody>
            {data.rows.map((r) => (
              <tr key={r.id}>
                <td>
                  <Link to={`/assessments/${r.id}`} className="strong">
                    {r.name}
                  </Link>
                  <div className="muted small">{r.sourceKind}</div>
                </td>
                <td>
                  <StatusChip status={r.status} />
                </td>
                <td className="num">
                  <ScoreBadge score={r.score} level={r.risk} />
                </td>
                <td className="num muted small">{r.percentile != null ? `${r.percentile}%` : "—"}</td>
                <td>
                  <RiskBadge level={r.risk} />
                </td>
                <td className={`small target-${r.targetStatus}`}>
                  {r.targetScore ? `${r.targetScore} · ${t(`target.${r.targetStatus}` as "target.Met")}` : "—"}
                </td>
                <td className="num">{r.openFindings ?? "—"}</td>
                <td className="num">{formatNumber(r.lines)}</td>
                <td className="num">{r.projects}</td>
                <td className="num">{r.legacyProjects}</td>
                <td className="small">{formatDate(r.completedAtUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </>
  );
}
