import { useEffect, useState } from "react";
import { api, type ActualOutcome, type ModernizationPlan, type Narrative, type RangeValue, type StrategyInfo } from "../api";
import { ErrorBox } from "../components";
import { Skeleton } from "../components/ui";
import { HBars, RoadmapGantt, useTokens } from "../components/charts";
import { FeedbackBar } from "../components/FeedbackBar";
import { Markdown } from "../components/Markdown";
import { useI18n } from "../i18n";

function money(v: number, currency: string, lang: string) {
  try {
    return new Intl.NumberFormat(lang, { style: "currency", currency, maximumFractionDigits: 0 }).format(v);
  } catch {
    return `${Math.round(v).toLocaleString(lang)} ${currency}`;
  }
}

export function ModernizationPanel({ assessmentId, refreshKey }: { assessmentId: string; refreshKey: string }) {
  const { t, term, lang, formatNumber, formatDate } = useI18n();
  const tk = useTokens();
  const [plan, setPlan] = useState<ModernizationPlan | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [actual, setActual] = useState<ActualOutcome | null>(null);
  const [form, setForm] = useState({ strategy: "", hours: "", months: "", cost: "", notes: "", by: "" });
  const [saved, setSaved] = useState(false);
  const [aiPlan, setAiPlan] = useState<Narrative | null>(null);
  const [planBusy, setPlanBusy] = useState(false);
  const [planMessage, setPlanMessage] = useState<string | null>(null);

  useEffect(() => {
    api.getActual(assessmentId, lang).then(setActual).catch(() => setActual(null));
    api
      .getMigrationPlan(assessmentId, lang)
      .then((p) => setAiPlan(p ?? null))
      .catch(() => setAiPlan(null));
  }, [assessmentId, lang]);

  async function draftPlan() {
    setPlanBusy(true);
    setPlanMessage(null);
    try {
      const r = await api.generateMigrationPlan(assessmentId, lang);
      setAiPlan(r);
      setPlanMessage(t("ai.planDone", { model: r.model }));
    } catch (err) {
      setPlanMessage(`${t("ai.planError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setPlanBusy(false);
    }
  }

  async function saveActual() {
    const hours = Number(form.hours);
    if (!form.strategy || !(hours > 0) || !form.by.trim()) return;
    try {
      const result = await api.recordActual(
        assessmentId,
        {
          strategy: form.strategy,
          actualHours: hours,
          actualMonths: form.months ? Number(form.months) : null,
          actualCost: form.cost ? Number(form.cost) : null,
          currency: null,
          notes: form.notes || null,
          recordedBy: form.by.trim(),
        },
        lang,
      );
      setActual(result);
      setSaved(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  useEffect(() => {
    setPlan(undefined);
    api
      .getModernization(assessmentId, lang)
      .then(setPlan)
      .catch(() => setError(t("error.load")));
  }, [assessmentId, lang, refreshKey, t]);

  if (error) return <ErrorBox message={error} />;
  if (plan === undefined) {
    return (
      <>
        <Skeleton kind="tile" count={4} />
        <Skeleton kind="block" />
      </>
    );
  }
  if (plan === null) return <p className="muted">{t("mod.none")}</p>;

  const rec = plan.strategies.find((s) => s.recommended)!;
  const e = rec.estimate;
  const range = (r: RangeValue, unit: string, digits = 0) =>
    `${formatNumber(Number(r.optimistic.toFixed(digits)))} – ${formatNumber(Number(r.conservative.toFixed(digits)))} ${unit}`;
  const p = plan.profile;

  return (
    <>
      <section className="card">
        <p className="eyebrow muted small">{t("mod.recommended")}</p>
        <h2 style={{ marginTop: 0 }}>{rec.name}</h2>
        <p className="muted">{rec.description}</p>
        <div className="mod-hero">
          <div className="tile">
            <span className="tile-v">{range(e.effortHours, t("mod.hours"))}</span>
            <span className="tile-l">
              {t("mod.effort")} · {t("mod.likely")} {formatNumber(e.effortHours.likely)} {t("mod.hours")}
            </span>
          </div>
          <div className="tile">
            <span className="tile-v">{range(e.durationMonths, t("mod.months"), 1)}</span>
            <span className="tile-l">
              {t("mod.duration")} · {t("mod.likely")} {e.durationMonths.likely} {t("mod.months")}
            </span>
          </div>
          <div className="tile">
            <span className="tile-v">
              {money(e.cost.optimistic, e.cost.currency, lang)} – {money(e.cost.conservative, e.cost.currency, lang)}
            </span>
            <span className="tile-l">
              {t("mod.cost")} · {t("mod.likely")} {money(e.cost.likely, e.cost.currency, lang)}
            </span>
          </div>
          <div className="tile">
            <span className="tile-v">{e.confidenceLabel}</span>
            <span className="tile-l">{t("mod.confidence")}</span>
          </div>
        </div>
      </section>

      <div className="grid-2">
        <section className="card">
          <h2>{t("mod.fitChart")}</h2>
          <p className="muted small">{t("mod.fitChartHint")}</p>
          <HBars
            height="h-md"
            max={100}
            labelWidth={190}
            data={plan.strategies.map((s) => ({ name: s.name, value: s.fitScore, color: s.recommended ? tk.accent : tk.faint, hint: `${t("mod.risk")}: ${term("risk", s.risk)}` }))}
          />
        </section>
        <section className="card">
          <h2>{t("mod.effortChart")}</h2>
          <p className="muted small">{t("mod.effortChartHint", { strategy: rec.name })}</p>
          <HBars
            height="h-md"
            labelWidth={190}
            data={[...e.breakdown].sort((a, b) => b.hours - a.hours).slice(0, 8).map((b) => ({ name: b.label, value: Math.round(b.hours), color: tk.accent }))}
            valueFormat={(v) => `${formatNumber(v)} ${t("mod.hours")}`}
          />
        </section>
      </div>

      <section className="card">
        <h2>{t("mod.strategies")}</h2>
        <table className="mod-strategies">
          <thead>
            <tr>
              <th>{t("mod.strategy")}</th>
              <th className="num">{t("mod.fit")}</th>
              <th>{t("mod.risk")}</th>
              <th className="num">{t("mod.effort")}</th>
              <th className="num">{t("mod.duration")}</th>
              <th className="num">{t("mod.cost")}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {plan.strategies.map((s: StrategyInfo) => (
              <>
                <tr key={s.strategy} className={s.recommended ? "recommended" : ""}>
                  <td>
                    <strong>{s.name}</strong>
                    {s.recommended ? " ★" : ""}
                    <div className="muted small">{s.description}</div>
                  </td>
                  <td className="num">{s.fitScore}</td>
                  <td className={`risk-${s.risk}`}>{term("risk", s.risk)}</td>
                  <td className="num">
                    {formatNumber(s.estimate.effortHours.likely)} {t("mod.hours")}
                  </td>
                  <td className="num">
                    {s.estimate.durationMonths.likely} {t("mod.months")}
                  </td>
                  <td className="num">{money(s.estimate.cost.likely, s.estimate.cost.currency, lang)}</td>
                  <td>
                    <button className="button small" onClick={() => setOpen(open === s.strategy ? null : s.strategy)}>
                      {t("mod.details")}
                    </button>
                  </td>
                </tr>
                {open === s.strategy && (
                  <tr key={s.strategy + "-details"} className="details">
                    <td colSpan={7}>
                      <div className="compare-grid">
                        <div>
                          <h3>{t("mod.why")}</h3>
                          <ul className="mod-list">{s.rationale.map((r) => <li key={r}>{r}</li>)}</ul>
                          {s.blockers.length > 0 && (
                            <>
                              <h3>{t("mod.blockers")}</h3>
                              <ul className="mod-list warn">{s.blockers.map((r) => <li key={r}>{r}</li>)}</ul>
                            </>
                          )}
                        </div>
                        <div>
                          <h3>{t("mod.prereqs")}</h3>
                          <ul className="mod-list">{s.prerequisites.map((r) => <li key={r}>{r}</li>)}</ul>
                          <h3>{t("mod.benefits")}</h3>
                          <ul className="mod-list">{s.benefits.map((r) => <li key={r}>{r}</li>)}</ul>
                        </div>
                      </div>
                      <p className="muted small">
                        {t("mod.effort")}: {range(s.estimate.effortHours, t("mod.hours"))} · {t("mod.duration")}: {range(s.estimate.durationMonths, t("mod.months"), 1)} ·{" "}
                        {t("mod.cost")}: {money(s.estimate.cost.optimistic, s.estimate.cost.currency, lang)} – {money(s.estimate.cost.conservative, s.estimate.cost.currency, lang)}
                      </p>
                    </td>
                  </tr>
                )}
              </>
            ))}
          </tbody>
        </table>
      </section>

      <div className="compare-grid">
        <section className="card">
          <h2>{t("mod.breakdown")}</h2>
          <table>
            <thead>
              <tr>
                <th />
                <th className="num">{t("mod.qty")}</th>
                <th className="num">{t("mod.likely")}</th>
              </tr>
            </thead>
            <tbody>
              {e.breakdown.map((b) => (
                <tr key={b.key}>
                  <td>{b.label}</td>
                  <td className="num">{b.quantity}</td>
                  <td className="num">
                    {formatNumber(b.hours)} {t("mod.hours")}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
        <section className="card">
          <h2>{t("mod.assumptions")}</h2>
          <table>
            <tbody>
              {e.assumptions.map((a) => (
                <tr key={a.key}>
                  <th style={{ textAlign: "left" }}>{a.label}</th>
                  <td className="mono">{a.value}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <h3>{t("mod.profile")}</h3>
          <table>
            <tbody>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.loc")}</th>
                <td className="num">{formatNumber(p.linesOfCode)}</td>
              </tr>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.projects")}</th>
                <td className="num">
                  {p.projects} ({p.legacyFrameworkProjects} / {p.modernFrameworkProjects} / {p.unknownFrameworkProjects})
                </td>
              </tr>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.blockers")}</th>
                <td className="num">
                  {p.prerequisiteBlockers} / {p.highBlockers} / {p.mediumBlockers}
                </td>
              </tr>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.security")}</th>
                <td className="num">
                  {p.criticalSecurity} / {p.highSecurity}
                </td>
              </tr>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.tests")}</th>
                <td className="num">{p.hasTests ? "✓" : t("mod.profile.noTests")}</td>
              </tr>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.coverage")}</th>
                <td className="num">{p.coverageLineRate === null ? t("mod.profile.unknown") : `${Math.round(p.coverageLineRate * 100)}%`}</td>
              </tr>
              <tr>
                <th style={{ textAlign: "left" }}>{t("mod.profile.cycles")}</th>
                <td className="num">{p.architectureCycles}</td>
              </tr>
            </tbody>
          </table>
        </section>
      </div>

      <section className="card">
        <h2>{t("mod.roadmap")}</h2>
        <RoadmapGantt phases={plan.roadmap.phases} unit={t("mod.months")} hint={t("mod.ganttHint")} />
        <table>
          <thead>
            <tr>
              <th>{t("mod.phase")}</th>
              <th className="num">{t("mod.share")}</th>
              <th className="num">{t("mod.effort")}</th>
              <th className="num">{t("mod.duration")}</th>
              <th>{t("mod.dependsOn")}</th>
              <th>{t("mod.work")}</th>
            </tr>
          </thead>
          <tbody>
            {plan.roadmap.phases.map((ph) => (
              <tr key={ph.key}>
                <td>
                  <strong>{ph.name}</strong>
                </td>
                <td className="num">{Math.round(ph.effortShare * 100)}%</td>
                <td className="num">{range(ph.effortHours, t("mod.hours"))}</td>
                <td className="num">{range(ph.durationMonths, t("mod.months"), 1)}</td>
                <td className="small">{ph.dependsOnNames.length === 0 ? "—" : ph.dependsOnNames.join(", ")}</td>
                <td className="small">
                  {ph.workItems.map((w) => (
                    <div key={w.key}>
                      {w.label}
                      {w.quantity > 1 ? ` ×${formatNumber(w.quantity)}` : ""}
                    </div>
                  ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="muted small">{t("mod.note", { versions: `${plan.modelVersion} · ${e.modelVersion} · ${plan.roadmap.modelVersion}` })}</p>
      </section>

      <section className="card">
        <h2>✨ {t("ai.planTitle")}</h2>
        <p className="muted small">{t("ai.planHint")}</p>
        <div className="actions">
          <button className="button primary" onClick={draftPlan} disabled={planBusy}>
            {planBusy ? t("ai.planGenerating") : aiPlan ? t("ai.planRegenerate") : t("ai.planGenerate")}
          </button>
          {aiPlan && (
            <a className="button" href={api.migrationPlanUrl(assessmentId, lang)}>
              ⬇ {t("ai.planExport")}
            </a>
          )}
        </div>
        {planMessage && <p className="banner small">{planMessage}</p>}
        {aiPlan ? (
          <div className="ai-box ai-plan">
            <Markdown text={aiPlan.text} />
            <div className="row">
              <small className="muted">{t("ai.planLabel", { model: aiPlan.model, when: formatDate(aiPlan.createdAtUtc) })}</small>
              <FeedbackBar rating={aiPlan.rating} onRate={async (rating, comment) => setAiPlan(await api.rateNarrative(assessmentId, "migration-plan", { rating, comment, author: null }, lang))} />
            </div>
          </div>
        ) : (
          <p className="muted small">{t("ai.planNone")}</p>
        )}
      </section>

      <section className="card">
        <h2>{t("mod.actual")}</h2>
        <p className="muted small">{t("mod.actualHint")}</p>
        {actual && (
          <p className="banner ok">
            {t("mod.actualRecorded", {
              hours: formatNumber(actual.actualHours),
              strategy: actual.strategyName,
              by: actual.recordedBy,
              estimated: formatNumber(plan.strategies.find((s) => s.strategy === actual.strategy)?.estimate.effortHours.likely ?? e.effortHours.likely),
              ratio: (actual.actualHours / (plan.strategies.find((s) => s.strategy === actual.strategy)?.estimate.effortHours.likely ?? e.effortHours.likely)).toFixed(2),
            })}
          </p>
        )}
        <div className="form" style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(12rem, 1fr))", gap: "0.6rem" }}>
          <label>
            <span>{t("mod.actualStrategy")}</span>
            <select value={form.strategy} onChange={(ev) => setForm({ ...form, strategy: ev.target.value })}>
              <option value="">—</option>
              {plan.strategies.map((s) => (
                <option key={s.strategy} value={s.strategy}>
                  {s.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>{t("mod.actualHours")}</span>
            <input type="number" min={1} value={form.hours} onChange={(ev) => setForm({ ...form, hours: ev.target.value })} />
          </label>
          <label>
            <span>{t("mod.actualMonths")}</span>
            <input type="number" min={0} step={0.5} value={form.months} onChange={(ev) => setForm({ ...form, months: ev.target.value })} />
          </label>
          <label>
            <span>{t("mod.actualCost")}</span>
            <input type="number" min={0} value={form.cost} onChange={(ev) => setForm({ ...form, cost: ev.target.value })} />
          </label>
          <label>
            <span>{t("mod.actualBy")}</span>
            <input value={form.by} onChange={(ev) => setForm({ ...form, by: ev.target.value })} />
          </label>
          <label style={{ gridColumn: "1 / -1" }}>
            <span>{t("mod.actualNotes")}</span>
            <input value={form.notes} onChange={(ev) => setForm({ ...form, notes: ev.target.value })} />
          </label>
        </div>
        <div className="actions">
          <button className="button primary" onClick={saveActual} disabled={!form.strategy || !(Number(form.hours) > 0) || !form.by.trim()}>
            {t("mod.actualSave")}
          </button>
          {saved && <span className="muted small">{t("mod.actualSaved")}</span>}
        </div>
      </section>
    </>
  );
}
