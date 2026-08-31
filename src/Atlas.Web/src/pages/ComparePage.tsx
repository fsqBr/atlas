import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, type AssessmentSummary, type SideBySide } from "../api";
import { ErrorBox, RiskBadge, ScoreBadge, Spinner } from "../components";
import { MirrorBars, useTokens } from "../components/charts";
import { useI18n } from "../i18n";

/** Two assessments, same rows: health, findings, size, stack, strategy — and the rules that set them apart. */
export function ComparePage() {
  const { t, lang, term, formatNumber, formatDate } = useI18n();
  const tk = useTokens();
  const [params, setParams] = useSearchParams();
  const [list, setList] = useState<AssessmentSummary[] | null>(null);
  const [data, setData] = useState<SideBySide | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const a = params.get("a") ?? "";
  const b = params.get("b") ?? "";

  useEffect(() => {
    api.listAssessments().then(setList).catch(() => setError(t("error.load")));
  }, [t]);

  useEffect(() => {
    if (!a || !b || a === b) {
      setData(null);
      return;
    }
    setLoading(true);
    api
      .compare(a, b, lang)
      .then((d) => {
        setData(d);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false));
  }, [a, b, lang]);

  function pick(side: "a" | "b", id: string) {
    const next = new URLSearchParams(params);
    if (id) next.set(side, id);
    else next.delete(side);
    setParams(next);
  }

  const delta = (x: number | null | undefined, y: number | null | undefined, higherIsBetter = true) => {
    if (x == null || y == null) return null;
    const d = y - x;
    if (d === 0) return <span className="muted">=</span>;
    const good = higherIsBetter ? d > 0 : d < 0;
    return <span className={good ? "delta-good" : "delta-bad"}>{d > 0 ? `+${formatNumber(d)}` : formatNumber(d)}</span>;
  };

  const Row = ({ label, va, vb, d }: { label: string; va: React.ReactNode; vb: React.ReactNode; d?: React.ReactNode }) => (
    <tr>
      <th>{label}</th>
      <td className="num">{va}</td>
      <td className="num">{vb}</td>
      <td className="num">{d ?? ""}</td>
    </tr>
  );

  return (
    <>
      <div className="page-head">
        <h1>{t("compare.title")}</h1>
      </div>
      <p className="muted">{t("compare.intro")}</p>
      {error && <ErrorBox message={error} />}

      <div className="card discover-row">
        {(["a", "b"] as const).map((side) => (
          <label key={side} style={{ flex: 1 }}>
            <span>{side === "a" ? t("compare.sideA") : t("compare.sideB")}</span>
            <select value={side === "a" ? a : b} onChange={(e) => pick(side, e.target.value)}>
              <option value="">—</option>
              {(list ?? []).map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </select>
          </label>
        ))}
      </div>

      {loading && <Spinner />}
      {a && b && a === b && <p className="banner small">{t("compare.same")}</p>}

      {data && (
        <>
          <section className="card">
            <table className="table compare">
              <thead>
                <tr>
                  <th></th>
                  <th className="num">
                    <Link to={`/assessments/${data.a.id}`}>{data.a.name}</Link>
                  </th>
                  <th className="num">
                    <Link to={`/assessments/${data.b.id}`}>{data.b.name}</Link>
                  </th>
                  <th className="num">Δ (B − A)</th>
                </tr>
              </thead>
              <tbody>
                <Row label={t("detail.completed")} va={formatDate(data.a.completedAtUtc)} vb={formatDate(data.b.completedAtUtc)} />
                <Row
                  label={t("list.score")}
                  va={<ScoreBadge score={data.a.score} level={data.a.risk} />}
                  vb={<ScoreBadge score={data.b.score} level={data.b.risk} />}
                  d={delta(data.a.score, data.b.score)}
                />
                <Row label={t("portfolio.risk")} va={<RiskBadge level={data.a.risk} />} vb={<RiskBadge level={data.b.risk} />} />
                {Object.keys({ ...data.a.dimensions, ...data.b.dimensions }).map((dim) => (
                  <Row key={dim} label={`↳ ${dim}`} va={data.a.dimensions[dim] ?? "—"} vb={data.b.dimensions[dim] ?? "—"} d={delta(data.a.dimensions[dim], data.b.dimensions[dim])} />
                ))}
                <Row label={t("list.findings")} va={formatNumber(data.a.openFindings)} vb={formatNumber(data.b.openFindings)} d={delta(data.a.openFindings, data.b.openFindings, false)} />
                {["Critical", "High", "Medium", "Low"].map((sev) => (
                  <Row
                    key={sev}
                    label={`↳ ${term("sev", sev)}`}
                    va={formatNumber(data.a.openBySeverity[sev] ?? 0)}
                    vb={formatNumber(data.b.openBySeverity[sev] ?? 0)}
                    d={delta(data.a.openBySeverity[sev] ?? 0, data.b.openBySeverity[sev] ?? 0, false)}
                  />
                ))}
                {Object.keys({ ...data.a.openByCategory, ...data.b.openByCategory })
                  .filter((c) => (data.a.openByCategory[c] ?? 0) + (data.b.openByCategory[c] ?? 0) > 0)
                  .map((c) => (
                    <Row key={c} label={`↳ ${term("cat", c)}`} va={formatNumber(data.a.openByCategory[c] ?? 0)} vb={formatNumber(data.b.openByCategory[c] ?? 0)} d={delta(data.a.openByCategory[c] ?? 0, data.b.openByCategory[c] ?? 0, false)} />
                  ))}
                <Row label={t("portfolio.lines")} va={formatNumber(data.a.lines)} vb={formatNumber(data.b.lines)} />
                <Row label={t("portfolio.projects")} va={data.a.projects} vb={data.b.projects} />
                <Row label={t("portfolio.legacyProjects")} va={data.a.legacyProjects} vb={data.b.legacyProjects} d={delta(data.a.legacyProjects, data.b.legacyProjects, false)} />
                <Row
                  label={t("compare.uiStack")}
                  va={Object.entries(data.a.uiFrameworks).map(([k, v]) => `${k} ×${v}`).join(", ") || "—"}
                  vb={Object.entries(data.b.uiFrameworks).map(([k, v]) => `${k} ×${v}`).join(", ") || "—"}
                />
                <Row label={t("compare.strategy")} va={data.a.recommendedStrategy ?? "—"} vb={data.b.recommendedStrategy ?? "—"} />
                <Row
                  label={t("compare.effort")}
                  va={data.a.likelyHours != null ? `${formatNumber(Math.round(data.a.likelyHours))} h` : "—"}
                  vb={data.b.likelyHours != null ? `${formatNumber(Math.round(data.b.likelyHours))} h` : "—"}
                  d={delta(data.a.likelyHours, data.b.likelyHours, false)}
                />
                <Row
                  label={t("compare.cost")}
                  va={data.a.likelyCost != null ? `${formatNumber(Math.round(data.a.likelyCost))} ${data.a.currency ?? ""}` : "—"}
                  vb={data.b.likelyCost != null ? `${formatNumber(Math.round(data.b.likelyCost))} ${data.b.currency ?? ""}` : "—"}
                />
                <Row label={t("portfolio.target")} va={data.a.targetScore ?? "—"} vb={data.b.targetScore ?? "—"} />
              </tbody>
            </table>
          </section>

          <div className="grid-2">
            <section className="card">
              <h2>{t("compare.dims")}</h2>
              <MirrorBars
                labelA={data.a.name}
                labelB={data.b.name}
                colorA={tk.low}
                colorB={tk.accent}
                rows={Object.keys({ ...data.a.dimensions, ...data.b.dimensions }).map((d) => ({ name: term("dim", d), a: data.a.dimensions[d] ?? 0, b: data.b.dimensions[d] ?? 0 }))}
              />
            </section>
            <section className="card">
              <h2>{t("compare.sev")}</h2>
              <MirrorBars
                labelA={data.a.name}
                labelB={data.b.name}
                colorA={tk.low}
                colorB={tk.accent}
                format={formatNumber}
                rows={["Critical", "High", "Medium", "Low"].map((sev) => ({ name: term("sev", sev), a: data.a.openBySeverity[sev] ?? 0, b: data.b.openBySeverity[sev] ?? 0 }))}
              />
            </section>
          </div>

          {data.ruleDifferences.length > 0 && (
            <section className="card">
              <h2>{t("compare.differences")}</h2>
              <p className="muted small">{t("compare.differencesHint")}</p>
              <table className="table">
                <thead>
                  <tr>
                    <th>{t("findings.severity")}</th>
                    <th>{t("findings.finding")}</th>
                    <th className="num">{data.a.name}</th>
                    <th className="num">{data.b.name}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.ruleDifferences.map((r) => (
                    <tr key={r.ruleId}>
                      <td>{term("sev", r.maxSeverity)}</td>
                      <td>
                        {r.title} <span className="muted small mono">{r.ruleId}</span>
                      </td>
                      <td className="num">{r.countA}</td>
                      <td className="num">{r.countB}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
          )}
        </>
      )}
    </>
  );
}
