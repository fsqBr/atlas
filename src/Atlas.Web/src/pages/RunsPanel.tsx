import { useEffect, useState } from "react";
import { api, type Run, type RunComparison, type RuleDelta } from "../api";
import { ErrorBox, SeverityChip, Spinner, StatusChip } from "../components";
import { StackedVBars, TrendLine, useTokens } from "../components/charts";
import { useI18n } from "../i18n";

function duration(start: string, end: string | null) {
  if (!end) return "…";
  const s = (new Date(end).getTime() - new Date(start).getTime()) / 1000;
  return s < 60 ? `${s.toFixed(0)} s` : `${(s / 60).toFixed(1)} min`;
}

function signed(n: number | null | undefined) {
  if (n === null || n === undefined) return "—";
  return n > 0 ? `+${n}` : `${n}`;
}

export function RunsPanel({ assessmentId, active, refreshKey }: { assessmentId: string; active: boolean; refreshKey: string }) {
  const { t, lang, formatDate } = useI18n();
  const [runs, setRuns] = useState<Run[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [withRun, setWithRun] = useState<string>("");
  const [comparison, setComparison] = useState<RunComparison | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const load = () =>
      api
        .listRuns(assessmentId)
        .then((list) => {
          if (!alive) return;
          setRuns(list);
          setSelected((current) => current ?? list.find((r) => r.status !== "Running")?.id ?? null);
        })
        .catch(() => alive && setError(t("error.load")));
    load();
    // While a run is queued or in progress, keep the history live so the new row appears and completes in place.
    const timer = active ? setInterval(load, 3000) : null;
    return () => {
      alive = false;
      if (timer) clearInterval(timer);
    };
  }, [assessmentId, refreshKey, active, t]);

  useEffect(() => {
    if (!selected) return;
    setComparison(null);
    api.compareRun(assessmentId, selected, lang, withRun || undefined).then(setComparison).catch(() => setError(t("error.load")));
  }, [assessmentId, selected, withRun, lang, t]);

  if (error) return <ErrorBox message={error} />;
  if (!runs) return <Spinner />;

  const previousScore = (run: Run) => {
    const older = runs.filter((r) => r.number < run.number).sort((a, b) => b.number - a.number)[0];
    return older?.healthScore ?? null;
  };

  return (
    <>
      <section className="card">
        <h2>{t("runs.title")}</h2>
        <RunCharts runs={runs} />
        {runs.length === 0 && <p className="muted">{t("runs.empty")}</p>}
        {runs.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>{t("runs.number")}</th>
                <th>{t("runs.started")}</th>
                <th>{t("runs.commit")}</th>
                <th>{t("runs.status")}</th>
                <th className="num">{t("runs.duration")}</th>
                <th className="num">{t("runs.score")}</th>
                <th className="num">{t("runs.new")}</th>
                <th className="num">{t("runs.resolved")}</th>
                <th className="num">{t("runs.regressed")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {runs.map((r) => {
                const prev = previousScore(r);
                const delta = r.healthScore !== null && prev !== null ? r.healthScore - prev : null;
                return (
                  <tr key={r.id} className={selected === r.id ? "expanded" : ""}>
                    <td className="strong">#{r.number}</td>
                    <td className="small">{formatDate(r.startedAtUtc)}</td>
                    <td className="mono small">{r.commitSha ? r.commitSha.slice(0, 10) : "—"}</td>
                    <td>
                      <StatusChip status={r.status} />
                    </td>
                    <td className="num muted">{duration(r.startedAtUtc, r.finishedAtUtc)}</td>
                    <td className="num">
                      {r.healthScore ?? "—"}
                      {delta !== null && delta !== 0 && (
                        <span className={`delta ${delta > 0 ? "up" : "down"}`}> {signed(delta)}</span>
                      )}
                    </td>
                    <td className="num">{r.findingsNew}</td>
                    <td className="num">{r.findingsResolved}</td>
                    <td className="num">{r.findingsRegressed}</td>
                    <td>
                      <button
                        className="button"
                        onClick={() => {
                          setSelected(r.id);
                          setWithRun("");
                        }}
                        disabled={selected === r.id}
                      >
                        {t("runs.compare")}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </section>

      {selected && runs.length > 1 && (
        <p className="compare-with">
          <label>
            {t("runs.compareWith")}{" "}
            <select value={withRun} onChange={(e) => setWithRun(e.target.value)}>
              <option value="">{t("runs.previous")}</option>
              {runs
                .filter((r) => r.id !== selected && r.status !== "Running")
                .map((r) => (
                  <option key={r.id} value={r.id}>
                    #{r.number} · {formatDate(r.startedAtUtc)}
                    {r.healthScore !== null ? ` · ${r.healthScore}` : ""}
                  </option>
                ))}
            </select>
          </label>
        </p>
      )}
      {selected && !comparison && <Spinner />}
      {comparison && <ComparisonPanel comparison={comparison} />}
    </>
  );
}

/** Health per run and the flow of findings (resolved / new / regressed) per run, oldest → newest. */
function RunCharts({ runs }: { runs: Run[] }) {
  const { t, formatDate } = useI18n();
  const tk = useTokens();
  const done = runs.filter((r) => r.status !== "Running").sort((a, b) => a.number - b.number);
  if (done.length < 2) return null;
  return (
    <div className="grid-2" style={{ marginBottom: "0.8rem" }}>
      <div>
        <p className="eyebrow">{t("runs.trend")}</p>
        <TrendLine height="h-sm" points={done.map((r) => ({ x: `#${r.number}`, value: r.healthScore, hint: `#${r.number} · ${formatDate(r.startedAtUtc)}` }))} />
      </div>
      <div>
        <p className="eyebrow">{t("runs.flow")}</p>
        <StackedVBars
          height="h-sm"
          data={done.map((r) => ({ name: `#${r.number}`, resolved: r.findingsResolved, new: r.findingsNew, regressed: r.findingsRegressed }))}
          keys={[
            { key: "resolved", label: t("runs.resolved"), color: tk.ok },
            { key: "new", label: t("runs.new"), color: tk.high },
            { key: "regressed", label: t("runs.regressed"), color: tk.medium },
          ]}
        />
      </div>
    </div>
  );
}

function ComparisonPanel({ comparison: c }: { comparison: RunComparison }) {
  const { t, term, formatNumber } = useI18n();
  const improved = c.dimensions.filter((d) => (d.delta ?? 0) > 0);
  const worsened = c.dimensions.filter((d) => (d.delta ?? 0) < 0);
  const nothingChanged = c.resolved.length + c.new.length + c.regressed.length === 0;

  return (
    <section className="card">
      <h2>{c.previous ? t("runs.comparing", { current: c.current.number, previous: c.previous.number }) : t("runs.first", { current: c.current.number })}</h2>

      {c.previous && (
        <p className="headline">
          {c.healthDelta !== null && (
            <span className={`delta-big ${c.healthDelta > 0 ? "up" : c.healthDelta < 0 ? "down" : ""}`}>
              {t("runs.summary.health", { before: c.previous.healthScore ?? "—", after: c.current.healthScore ?? "—", delta: signed(c.healthDelta) })}
            </span>
          )}
          <span className="muted">
            {" · "}
            {t("runs.summary.counts", { resolved: c.resolved.reduce((n, r) => n + r.count, 0), new: c.new.reduce((n, r) => n + r.count, 0), regressed: c.regressed.reduce((n, r) => n + r.count, 0) })}
          </span>
        </p>
      )}

      {c.sameCommit && <p className="muted small">{t("runs.sameCommit")}</p>}

      {c.inventory && (
        <p className="muted small">
          {t("runs.inventory", {
            linesBefore: formatNumber(c.inventory.linesBefore),
            linesAfter: formatNumber(c.inventory.linesAfter),
            filesBefore: formatNumber(c.inventory.filesBefore),
            filesAfter: formatNumber(c.inventory.filesAfter),
            projectsBefore: c.inventory.projectsBefore,
            projectsAfter: c.inventory.projectsAfter,
          })}
        </p>
      )}

      {c.previous && (
        <div className="compare-grid">
          <div>
            <h3 className="up">{t("runs.improved")}</h3>
            {improved.length === 0 ? <p className="muted small">{t("runs.none")}</p> : <DimensionList items={improved} />}
          </div>
          <div>
            <h3 className="down">{t("runs.worsened")}</h3>
            {worsened.length === 0 ? <p className="muted small">{t("runs.none")}</p> : <DimensionList items={worsened} />}
          </div>
        </div>
      )}

      <h3>{t("runs.dimensions")}</h3>
      <table>
        <thead>
          <tr>
            <th>{t("health.dimension")}</th>
            <th className="num">{t("runs.before")}</th>
            <th className="num">{t("runs.after")}</th>
            <th className="num">Δ</th>
          </tr>
        </thead>
        <tbody>
          {c.dimensions.map((d) => (
            <tr key={d.name}>
              <td>
                <strong>{term("dim", d.name)}</strong>
              </td>
              <td className="num muted">{d.before ?? "—"}</td>
              <td className="num">{d.after}</td>
              <td className={`num delta ${(d.delta ?? 0) > 0 ? "up" : (d.delta ?? 0) < 0 ? "down" : ""}`}>{signed(d.delta)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {nothingChanged && c.previous && <p className="muted">{t("runs.noChange")}</p>}
      <RuleList title={t("runs.resolvedList")} items={c.resolved} tone="up" />
      <RuleList title={t("runs.newList")} items={c.new} tone="down" />
      <RuleList title={t("runs.regressedList")} items={c.regressed} tone="down" />
    </section>
  );
}

function DimensionList({ items }: { items: { name: string; before: number | null; after: number; delta: number | null }[] }) {
  const { term } = useI18n();
  return (
    <ul className="dimlist">
      {items.map((d) => (
        <li key={d.name}>
          <strong>{term("dim", d.name)}</strong> {d.before} → {d.after}{" "}
          <span className={`delta ${(d.delta ?? 0) > 0 ? "up" : "down"}`}>{signed(d.delta)}</span>
        </li>
      ))}
    </ul>
  );
}

function RuleList({ title, items, tone }: { title: string; items: RuleDelta[]; tone: "up" | "down" }) {
  const { term } = useI18n();
  if (items.length === 0) return null;
  return (
    <>
      <h3 className={tone}>
        {title} <span className="muted">({items.reduce((n, r) => n + r.count, 0)})</span>
      </h3>
      <table>
        <tbody>
          {items.map((r) => (
            <tr key={r.ruleId}>
              <td>
                <SeverityChip severity={r.maxSeverity} />
              </td>
              <td>
                <div className="strong">
                  {r.title} <span className="muted">×{r.count}</span>
                </div>
                <div className="mono small muted">
                  {r.ruleId} · {term("cat", r.category)}
                </div>
              </td>
              <td className="mono small">{r.sampleLocations.map((l) => <div key={l}>{l}</div>)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
