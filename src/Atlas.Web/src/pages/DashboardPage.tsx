import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, type AssessmentSummary, type Portfolio } from "../api";
import { ErrorBox, RiskBadge, StatusChip } from "../components";
import { Donut, HBars, Legend, RISKS, ScoreRing, SEVERITIES, severityColor, useTokens } from "../components/charts";
import { Card, EmptyState, PageHeader, Skeleton, Tile, riskTone, scoreTone } from "../components/ui";
import { useI18n } from "../i18n";

const CATEGORIES = ["Security", "Secrets", "Data", "Modernization", "Dependencies", "Architecture", "Quality"];

/** Home: the portfolio at a glance — headline numbers, distributions, what needs attention, and every assessment as a card. */
export function DashboardPage() {
  const { t, term, lang, formatNumber, formatDate } = useI18n();
  const tk = useTokens();
  const [portfolio, setPortfolio] = useState<Portfolio | null>(null);
  const [items, setItems] = useState<AssessmentSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const load = () => {
      api.listAssessments().then((d) => alive && (setItems(d), setError(null))).catch(() => alive && setError(t("error.load")));
      api.getPortfolio(lang).then((p) => alive && setPortfolio(p)).catch(() => alive && setError(t("error.load")));
    };
    load();
    const timer = setInterval(load, 8000);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, [lang, t]);

  const rowsById = useMemo(() => new Map((portfolio?.rows ?? []).map((r) => [r.id, r])), [portfolio]);

  const attention = useMemo(() => {
    if (!portfolio) return [];
    return portfolio.rows
      .map((r) => {
        const why: string[] = [];
        if (r.status === "Failed") why.push(t("dash.why.failed"));
        if (r.risk === "Critical" || r.risk === "High") why.push(t("dash.why.risk", { risk: term("risk", r.risk) }));
        if (r.targetStatus === "Missed" || r.targetStatus === "AtRisk") why.push(t("dash.why.target", { status: t(`target.${r.targetStatus}` as "target.Met"), target: r.targetScore ?? "" }));
        if ((r.openFindings ?? 0) > 200) why.push(t("dash.why.volume", { n: formatNumber(r.openFindings ?? 0) }));
        return { r, why };
      })
      .filter((x) => x.why.length > 0)
      .sort((a, b) => (a.r.score ?? 101) - (b.r.score ?? 101))
      .slice(0, 6);
  }, [portfolio, t, term, formatNumber]);

  const [demoBusy, setDemoBusy] = useState(false);
  const hasDemo = (items ?? []).some((a) => a.sourceLocator.startsWith("demo://"));

  if (error && !items) return <ErrorBox message={error} />;

  async function seedDemo() {
    setDemoBusy(true);
    try {
      await api.seedDemo();
      window.location.reload();
    } catch {
      setDemoBusy(false);
    }
  }

  if (items && items.length === 0) {
    return (
      <>
        {hasDemo && (
        <p className="muted small">
          <button
            type="button"
            className="button small"
            disabled={demoBusy}
            onClick={async () => {
              setDemoBusy(true);
              try {
                await api.removeDemo();
                window.location.reload();
              } catch {
                setDemoBusy(false);
              }
            }}
          >
            {demoBusy ? t("demo.busy") : t("demo.remove")}
          </button>
        </p>
      )}
      <PageHeader title={t("dash.title")} subtitle={t("dash.subtitle")} />
        <EmptyState
          glyph="◈"
          title={t("dash.empty.title")}
          text={t("dash.empty.text")}
          action={
            <span className="actions">
              <Link to="/new" className="button primary">{t("nav.new")}</Link>
              <button type="button" className="button" disabled={demoBusy} onClick={() => void seedDemo()}>
                {demoBusy ? t("demo.busy") : t("demo.load")}
              </button>
            </span>
          }
        >
          <ol className="steps">
            <li><b>1</b>{t("dash.empty.step1")}</li>
            <li><b>2</b>{t("dash.empty.step2")}</li>
            <li><b>3</b>{t("dash.empty.step3")}</li>
          </ol>
        </EmptyState>
      </>
    );
  }

  const p = portfolio;
  const legacyShare = p && p.projects > 0 ? Math.round((p.legacyProjects / p.projects) * 100) : null;
  const running = (items ?? []).filter((a) => a.activeJobState || a.status === "Running" || a.status === "Created").length;

  return (
    <>
      <PageHeader
        title={t("dash.title")}
        subtitle={p ? t("dash.subtitle.counts", { assessed: p.assessed, total: p.assessments, running }) : t("dash.subtitle")}
        actions={
          <>
            <Link to="/portfolio" className="button">{t("dash.portfolioDetail")}</Link>
            <Link to="/new" className="button primary">＋ {t("nav.new")}</Link>
          </>
        }
      />

      {!p ? (
        <Skeleton kind="tile" count={6} />
      ) : (
        <div className="kpis">
          <Tile value={p.averageScore ?? "—"} unit={p.averageScore !== null ? "/100" : undefined} label={t("portfolio.avgScore")} tone={scoreTone(p.averageScore)} hint={t("dash.assessedHint", { n: p.assessed })} />
          <Tile value={formatNumber(p.openBySeverity.Critical ?? 0)} label={t("dash.critical")} tone={(p.openBySeverity.Critical ?? 0) > 0 ? "critical" : "ok"} hint={t("dash.acrossPortfolio")} />
          <Tile value={formatNumber(p.openBySeverity.High ?? 0)} label={t("dash.high")} tone={(p.openBySeverity.High ?? 0) > 0 ? "high" : "ok"} hint={t("dash.acrossPortfolio")} />
          <Tile value={formatNumber(p.openFindings)} label={t("portfolio.openFindings")} tone="accent" />
          <Tile value={legacyShare === null ? "—" : `${legacyShare}%`} label={t("dash.legacyShare")} tone={legacyShare === null ? "neutral" : legacyShare > 50 ? "high" : legacyShare > 20 ? "medium" : "ok"} hint={t("dash.legacyHint", { legacy: p.legacyProjects, total: p.projects })} />
          <Tile value={formatNumber(p.lines)} label={t("portfolio.lines")} tone="neutral" hint={t("dash.projectsHint", { n: p.projects })} />
        </div>
      )}

      <div className="grid-3">
        <Card title={t("dash.byRisk")} subtitle={t("dash.byRiskHint")}>
          {p ? (
            <>
              <Donut
                data={RISKS.map((r) => ({ key: r, name: term("risk", r), value: p.byRisk[r] ?? 0 }))}
                colors={(k) => severityColor(tk, k)}
                centerLabel={t("dash.assessments")}
                emptyText={t("dash.noneAssessed")}
                height="h-sm"
              />
              <Legend items={RISKS.map((r) => ({ label: term("risk", r), color: severityColor(tk, r), value: p.byRisk[r] ?? 0 }))} />
            </>
          ) : (
            <Skeleton kind="block" />
          )}
        </Card>
        <Card title={t("dash.bySeverity")} subtitle={t("dash.openOnly")}>
          {p ? (
            <HBars height="h-sm" data={SEVERITIES.map((s) => ({ name: term("sev", s), value: p.openBySeverity[s] ?? 0, color: severityColor(tk, s) }))} valueFormat={formatNumber} labelWidth={84} />
          ) : (
            <Skeleton kind="block" />
          )}
        </Card>
        <Card title={t("dash.byCategory")} subtitle={t("dash.openOnly")}>
          {p ? (
            <HBars
              height="h-sm"
              data={CATEGORIES.map((c) => ({ name: term("cat", c), value: p.openByCategory[c] ?? 0, color: tk.accent })).filter((d) => d.value > 0).sort((a, b) => b.value - a.value)}
              valueFormat={formatNumber}
              labelWidth={96}
              emptyText={t("portfolio.noFindings")}
            />
          ) : (
            <Skeleton kind="block" />
          )}
        </Card>
      </div>

      <div className="grid-2">
        <Card title={t("dash.attention")} subtitle={t("dash.attentionHint")}>
          {!p ? (
            <Skeleton kind="line" count={4} />
          ) : attention.length === 0 ? (
            <p className="muted">{t("dash.attentionNone")}</p>
          ) : (
            <ul className="attention">
              {attention.map(({ r, why }) => (
                <li key={r.id}>
                  <ScoreRing score={r.score} risk={r.risk} size={52} stroke={6} caption="" />
                  <div>
                    <Link to={`/assessments/${r.id}`} className="strong">{r.name}</Link>
                    <div className="why">{why.join(" · ")}</div>
                  </div>
                  <RiskBadge level={r.risk} />
                </li>
              ))}
            </ul>
          )}
        </Card>
        <Card title={t("dash.frameworks")} subtitle={t("dash.frameworksHint")}>
          {p ? (
            <>
              <HBars
                height="h-md"
                data={p.frameworks.slice(0, 8).map((f) => ({
                  name: f.framework,
                  value: f.count,
                  color: f.framework === "unknown" ? tk.unknown : f.legacy ? tk.legacy : tk.modern,
                  hint: f.framework === "unknown" ? t("portfolio.unknown") : f.legacy ? t("portfolio.legacy") : t("portfolio.modern"),
                }))}
                labelWidth={110}
                emptyText={t("mod.none")}
              />
              <Legend items={[{ label: t("portfolio.legacy"), color: tk.legacy, value: p.legacyProjects }, { label: t("portfolio.modern"), color: tk.modern, value: p.modernProjects }, { label: t("portfolio.unknown"), color: tk.unknown, value: p.unknownProjects }]} />
            </>
          ) : (
            <Skeleton kind="block" />
          )}
        </Card>
      </div>

      <Card title={t("dash.assessments")} actions={<Link to="/assessments" className="button small">{t("dash.viewAll")} →</Link>}>
        {!items ? (
          <Skeleton kind="line" count={3} />
        ) : (
          <div className="a-cards">
            {items.map((a) => {
              const row = rowsById.get(a.id);
              const busy = !!a.activeJobState || a.status === "Running" || a.status === "Created";
              return (
                <Link key={a.id} to={`/assessments/${a.id}`} className={`a-card tone-${riskTone(a.riskLevel)}`}>
                  <ScoreRing score={a.healthScore} risk={a.riskLevel} size={64} stroke={7} caption="" />
                  <div style={{ minWidth: 0 }}>
                    <div className="name" title={a.name}>{a.name}</div>
                    <div className="meta">
                      <span className="kind">{a.sourceKind}</span>
                      <StatusChip status={a.status} />
                      {busy && <span className="pending"><span className="dot" /> {a.activeJobState === "Queued" ? t("list.queued") : t("list.running")}</span>}
                    </div>
                    <div className="facts">
                      <span><b>{a.openFindings === null ? "—" : formatNumber(a.openFindings)}</b> {t("list.findings").toLowerCase()}</span>
                      {row && row.projects > 0 && <span><b>{row.projects}</b> {t("portfolio.projects").toLowerCase()}</span>}
                      {row?.percentile != null && <span title={t("portfolio.benchmarkHint")}><b>P{row.percentile}</b></span>}
                      <span className="faint">{formatDate(a.completedAtUtc ?? a.createdAtUtc)}</span>
                    </div>
                  </div>
                </Link>
              );
            })}
          </div>
        )}
      </Card>
    </>
  );
}
