import { useEffect, useMemo, useState } from "react";
import { api, type Assessment, type Health, type HeatmapRow, type ModernizationPlan, type RuleGroup, type Run } from "../api";
import { HealthCard, SeverityChip, StatusChip } from "../components";
import { Donut, HBars, Legend, ScoreRing, SEVERITIES, severityColor, toneColor, TrendLine, useTokens } from "../components/charts";
import { Card, Skeleton, Tile, scoreTone } from "../components/ui";
import { useI18n } from "../i18n";

type Tab = "overview" | "findings" | "runs" | "modernization" | "rules" | "report" | "settings";

const SEV_RANK: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3, Informational: 4 };

function duration(start: string, end: string | null) {
  if (!end) return "…";
  const ms = new Date(end).getTime() - new Date(start).getTime();
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** Page one of the report, live: the score with its verdict and trend, the numbers that matter, and the recommended path. */
export function OverviewPanel({ assessment, health, active, onGoTo }: { assessment: Assessment; health: Health | null; active: boolean; onGoTo: (tab: Tab) => void }) {
  const { t, term, lang, formatNumber, formatDate } = useI18n();
  const tk = useTokens();
  const id = assessment.id;
  const [groups, setGroups] = useState<RuleGroup[] | null>(null);
  const [heat, setHeat] = useState<HeatmapRow[] | null>(null);
  const [runs, setRuns] = useState<Run[] | null>(null);
  const [plan, setPlan] = useState<ModernizationPlan | null | undefined>(undefined);
  const refreshKey = `${assessment.completedAtUtc ?? ""}:${active}`;

  useEffect(() => {
    let alive = true;
    api.findingsByRule(id, lang).then((g) => alive && setGroups(g)).catch(() => alive && setGroups([]));
    api.findingsHeatmap(id, 1).then((h) => alive && setHeat(h)).catch(() => alive && setHeat([]));
    api.listRuns(id).then((r) => alive && setRuns(r)).catch(() => alive && setRuns([]));
    api.getModernization(id, lang).then((p) => alive && setPlan(p)).catch(() => alive && setPlan(null));
    return () => {
      alive = false;
    };
  }, [id, lang, refreshKey]);

  const severity = useMemo(() => {
    const out: Record<string, number> = { Critical: 0, High: 0, Medium: 0, Low: 0, Informational: 0 };
    for (const row of heat ?? []) {
      out.Critical += row.critical; out.High += row.high; out.Medium += row.medium; out.Low += row.low; out.Informational += row.informational;
    }
    return out;
  }, [heat]);

  const categories = useMemo(() => {
    const out = new Map<string, number>();
    for (const g of groups ?? []) out.set(g.category, (out.get(g.category) ?? 0) + g.count);
    return [...out.entries()].sort((a, b) => b[1] - a[1]);
  }, [groups]);

  const topRisks = useMemo(
    () => [...(groups ?? [])].sort((a, b) => (SEV_RANK[a.maxSeverity] ?? 9) - (SEV_RANK[b.maxSeverity] ?? 9) || b.count - a.count).slice(0, 6),
    [groups],
  );

  const trend = useMemo(
    () =>
      [...(runs ?? [])]
        .filter((r) => r.status !== "Running")
        .sort((a, b) => a.number - b.number)
        .map((r) => ({ x: `#${r.number}`, value: r.healthScore, hint: `#${r.number} · ${formatDate(r.startedAtUtc)}` })),
    [runs, formatDate],
  );

  const rec = plan?.strategies.find((s) => s.recommended);
  const profile = plan?.profile;
  const openTotal = Object.values(severity).reduce((s, v) => s + v, 0);
  const previous = trend.length >= 2 ? trend[trend.length - 2].value : null;
  const delta = health && previous !== null ? health.score - previous : null;

  // A completed run with no findings AND no projects/files means nothing analyzable was found —
  // not a perfect estate. Distinguish it from a genuine "clean, low risk" verdict.
  const nothingAssessed = !!health && health.openFindings === 0 && health.projectCount === 0;
  const verdict = health
    ? nothingAssessed
      ? t("overview.verdictEmpty", { name: assessment.name })
      : t(`overview.verdict.${health.riskLevel}` as "overview.verdict.High", { name: assessment.name, score: health.score })
    : active
      ? t("overview.verdictRunning")
      : t("overview.verdictNone");

  const worstDimension = health ? [...health.dimensions].sort((a, b) => a.score - b.score)[0] : null;

  return (
    <>
      <Card>
        <div className="hero">
          <ScoreRing score={health?.score} risk={health?.riskLevel} size={148} stroke={12} />
          <div>
            <p className="eyebrow">{t("overview.headline")}</p>
            <p className="verdict">{verdict}</p>
            {health && worstDimension && (
              <p className="hero-meta">
                {t("overview.weakest", { dimension: term("dim", worstDimension.name), score: worstDimension.score })}
                {rec && <> · {t("overview.path", { strategy: rec.name, months: rec.estimate.durationMonths.likely })}</>}
                {delta !== null && delta !== 0 && <> · <span className={`delta ${delta > 0 ? "up" : "down"}`}>{delta > 0 ? `▲ +${delta}` : `▼ ${delta}`} {t("overview.sinceLastRun")}</span></>}
              </p>
            )}
            {health && (
              <p className="hero-meta small faint">
                {formatNumber(health.openFindings)} {t("health.open")} · {formatNumber(health.projectCount)} {t("health.projects")} · <span className="mono">{health.modelVersion}</span> · {formatDate(health.createdAtUtc)}
              </p>
            )}
          </div>
          <div className="hero-trend">
            <p className="eyebrow">{t("overview.trend")}</p>
            {runs === null ? <Skeleton kind="block" /> : <TrendLine points={trend} height="h-sm" target={assessment.targetScore} emptyText={t("overview.trendNone")} />}
          </div>
        </div>
      </Card>

      {profile === undefined ? (
        <Skeleton kind="tile" count={6} />
      ) : (
        <div className="kpis">
          <Tile value={formatNumber(severity.Critical)} label={t("dash.critical")} tone={severity.Critical > 0 ? "critical" : "ok"} onClick={() => onGoTo("findings")} title={t("overview.goFindings")} />
          <Tile value={formatNumber(severity.High)} label={t("dash.high")} tone={severity.High > 0 ? "high" : "ok"} onClick={() => onGoTo("findings")} title={t("overview.goFindings")} />
          <Tile
            value={profile ? formatNumber(profile.prerequisiteBlockers + profile.highBlockers) : "—"}
            label={t("overview.blockers")}
            hint={profile ? t("overview.blockersHint", { medium: profile.mediumBlockers, projects: profile.projectsWithBlockers }) : undefined}
            tone={!profile ? "neutral" : profile.prerequisiteBlockers + profile.highBlockers > 0 ? "high" : "ok"}
            onClick={() => onGoTo("modernization")}
          />
          <Tile
            value={profile ? `${profile.legacyFrameworkProjects}/${profile.projects}` : "—"}
            label={t("overview.legacyProjects")}
            tone={!profile ? "neutral" : profile.legacyFrameworkProjects === 0 ? "ok" : profile.legacyFrameworkProjects / Math.max(1, profile.projects) > 0.5 ? "high" : "medium"}
            hint={profile ? t("overview.legacyHint", { modern: profile.modernFrameworkProjects }) : undefined}
          />
          <Tile
            value={!profile ? "—" : !profile.hasTests ? t("overview.noTests") : profile.coverageLineRate === null ? "✓" : `${Math.round(profile.coverageLineRate * 100)}%`}
            label={t("overview.tests")}
            tone={!profile ? "neutral" : !profile.hasTests ? "high" : profile.coverageLineRate !== null && profile.coverageLineRate < 0.3 ? "medium" : "ok"}
            hint={profile ? (profile.hasTests ? (profile.coverageLineRate === null ? t("overview.coverageUnknown") : t("overview.coverage")) : t("overview.noTestsHint")) : undefined}
          />
          <Tile
            value={profile ? formatNumber(profile.vulnerablePackages) : "—"}
            label={t("overview.vulnerable")}
            tone={!profile ? "neutral" : profile.vulnerablePackages > 0 ? "high" : "ok"}
            hint={profile ? t("overview.secretsHint", { n: profile.secretsFound }) : undefined}
            onClick={() => onGoTo("findings")}
          />
        </div>
      )}

      <div className="grid-3">
        <Card title={t("overview.dimensions")} subtitle={t("overview.dimensionsHint")}>
          {health ? (
            <HBars
              height="h-md"
              max={100}
              data={health.dimensions.map((d) => ({ name: term("dim", d.name), value: d.score, color: toneColor(tk, scoreTone(d.score)), hint: t("overview.weight", { w: Math.round(d.weight * 100) }) }))}
              labelWidth={100}
            />
          ) : (
            <p className="muted">{t("health.none")}</p>
          )}
        </Card>
        <Card title={t("dash.bySeverity")} subtitle={t("overview.openOnly")}>
          {heat === null ? (
            <Skeleton kind="block" />
          ) : (
            <>
              <Donut
                height="h-sm"
                data={SEVERITIES.map((s) => ({ key: s, name: term("sev", s), value: severity[s] }))}
                colors={(k) => severityColor(tk, k)}
                centerLabel={t("health.open")}
                emptyText={t("overview.noOpen")}
              />
              <Legend items={SEVERITIES.filter((s) => severity[s] > 0).map((s) => ({ label: term("sev", s), color: severityColor(tk, s), value: severity[s] }))} />
            </>
          )}
        </Card>
        <Card title={t("dash.byCategory")} subtitle={t("overview.openOnly")}>
          {groups === null ? (
            <Skeleton kind="block" />
          ) : (
            <HBars height="h-md" data={categories.map(([c, n]) => ({ name: term("cat", c), value: n, color: tk.accent }))} labelWidth={96} valueFormat={formatNumber} emptyText={t("overview.noOpen")} />
          )}
        </Card>
      </div>

      <div className="grid-2">
        <Card title={t("overview.topRisks")} subtitle={t("overview.topRisksHint", { n: openTotal })} actions={<button className="button small" onClick={() => onGoTo("findings")}>{t("overview.goFindings")} →</button>}>
          {groups === null ? (
            <Skeleton kind="line" count={5} />
          ) : topRisks.length === 0 ? (
            <p className="muted">{t("overview.noOpen")}</p>
          ) : (
            <ul className="attention">
              {topRisks.map((g) => (
                <li key={g.ruleId}>
                  <SeverityChip severity={g.maxSeverity} />
                  <div>
                    <div className="strong">{g.title}</div>
                    <div className="why mono">{g.ruleId} · {term("cat", g.category)}{g.sampleFiles[0] ? ` · ${g.sampleFiles[0]}` : ""}</div>
                  </div>
                  <span className="num strong">×{formatNumber(g.count)}</span>
                </li>
              ))}
            </ul>
          )}
        </Card>
        <Card title={t("overview.strategy")} subtitle={t("overview.strategyHint")} actions={<button className="button small" onClick={() => onGoTo("modernization")}>{t("detail.tab.modernization")} →</button>}>
          {plan === undefined ? (
            <Skeleton kind="line" count={5} />
          ) : !rec ? (
            <p className="muted">{t("mod.none")}</p>
          ) : (
            <>
              <p className="eyebrow">{t("mod.recommended")}</p>
              <h3 style={{ fontSize: "1.25rem", margin: "0 0 0.2rem" }}>{rec.name}</h3>
              <p className="muted small" style={{ margin: "0 0 0.8rem" }}>{rec.description}</p>
              <div className="kpis" style={{ margin: 0 }}>
                <Tile value={formatNumber(rec.estimate.effortHours.likely)} unit="h" label={t("mod.effort")} tone="accent" hint={`${formatNumber(rec.estimate.effortHours.optimistic)} – ${formatNumber(rec.estimate.effortHours.conservative)} h`} />
                <Tile value={rec.estimate.durationMonths.likely} unit={t("mod.months")} label={t("mod.duration")} tone="accent" hint={`${rec.estimate.durationMonths.optimistic} – ${rec.estimate.durationMonths.conservative}`} />
                <Tile value={rec.fitScore} unit="/100" label={t("mod.fit")} tone={scoreTone(rec.fitScore)} hint={`${t("mod.confidence")}: ${rec.estimate.confidenceLabel}`} />
              </div>
              {rec.rationale.length > 0 && (
                <ul className="mod-list" style={{ marginTop: "0.8rem" }}>
                  {rec.rationale.slice(0, 3).map((r) => <li key={r}>{r}</li>)}
                </ul>
              )}
            </>
          )}
        </Card>
      </div>

      <details className="more card">
        <summary>{t("overview.healthDetail")}</summary>
        <div style={{ marginTop: "0.8rem" }}>
          <HealthCard health={health} />
          <h3>{t("scans.title")}</h3>
          <table>
            <thead>
              <tr>
                <th>{t("scans.scanner")}</th>
                <th>{t("scans.status")}</th>
                <th className="num">{t("scans.emitted")}</th>
                <th className="num">{t("scans.new")}</th>
                <th className="num">{t("scans.recurring")}</th>
                <th className="num">{t("scans.resolved")}</th>
                <th className="num">{t("scans.regressed")}</th>
                <th className="num">{t("scans.duration")}</th>
              </tr>
            </thead>
            <tbody>
              {assessment.scans.map((s) => (
                <tr key={s.id}>
                  <td className="mono">{s.scannerId} <span className="muted small">{s.scannerVersion}</span></td>
                  <td><StatusChip status={s.status} />{s.error && <div className="muted small">{s.error}</div>}</td>
                  <td className="num">{formatNumber(s.findingsEmitted)}</td>
                  <td className="num">{formatNumber(s.findingsNew)}</td>
                  <td className="num">{formatNumber(s.findingsRecurring)}</td>
                  <td className="num">{formatNumber(s.findingsResolved)}</td>
                  <td className="num">{formatNumber(s.findingsRegressed)}</td>
                  <td className="num muted">{duration(s.startedAtUtc, s.finishedAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </>
  );
}
