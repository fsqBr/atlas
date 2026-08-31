import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, downloadUrl, type AiEstimate, type BusinessRules } from "../api";
import { ErrorBox, Spinner } from "../components";
import { FeedbackBar } from "../components/FeedbackBar";
import { useI18n } from "../i18n";

/** Business rules recovered by the configured model; every rule links back to file + member. */
export function BusinessRulesPanel({ assessmentId, active }: { assessmentId: string; active: boolean }) {
  const { t, lang, formatDate } = useI18n();
  const [data, setData] = useState<BusinessRules | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [queuing, setQueuing] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [category, setCategory] = useState("");
  const [estimate, setEstimate] = useState<AiEstimate | null>(null);

  useEffect(() => {
    api.aiEstimate().then(setEstimate).catch(() => setEstimate(null));
  }, []);

  const load = useCallback(() => {
    api
      .businessRules(assessmentId, lang)
      .then((r) => {
        setData(r);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [assessmentId, lang]);

  useEffect(() => {
    load();
  }, [load]);

  // While a job is active on the assessment, poll for the analysis to land.
  useEffect(() => {
    if (!active) return;
    const id = window.setInterval(load, 5000);
    return () => window.clearInterval(id);
  }, [active, load]);

  async function analyze() {
    setQueuing(true);
    setMessage(null);
    try {
      await api.analyzeBusinessRules(assessmentId);
      setMessage(t("rules.queued"));
      load();
    } catch (err) {
      setMessage(`${t("rules.queueError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setQueuing(false);
    }
  }

  if (error) return <ErrorBox message={error} />;
  if (!data) return <Spinner />;

  const latest = data.analyses[0];
  const running = latest?.status === "Running";
  const categories = Array.from(new Set(data.rules.map((r) => r.category))).sort();
  const rules = data.rules.filter(
    (r) =>
      (!category || r.category === category) &&
      (!filter || `${r.name} ${r.description} ${r.filePath} ${r.symbol}`.toLowerCase().includes(filter.toLowerCase())),
  );
  const byFile = rules.reduce<Record<string, typeof rules>>((acc, r) => {
    (acc[r.filePath] ??= []).push(r);
    return acc;
  }, {});

  return (
    <div className="panel">
      <div className="toolbar">
        <div>
          <button className="button primary" onClick={analyze} disabled={queuing || active || running || !data.aiUsable}>
            {queuing ? t("rules.queuing") : running || active ? t("rules.running") : data.rules.length > 0 ? t("rules.reanalyze") : t("rules.analyze")}
          </button>
          {!data.aiUsable && (
            <span className="muted small">
              {" "}
              {t("rules.notConfigured")} <Link to="/settings/ai">{t("nav.ai")}</Link>
            </span>
          )}
        </div>
        {data.rules.length > 0 && (
          <div className="export">
            <a className="button small" href={downloadUrl(`/api/assessments/${assessmentId}/business-rules/export?format=csv&lang=${lang}`)}>
              CSV
            </a>
            <a className="button small" href={downloadUrl(`/api/assessments/${assessmentId}/business-rules/export?format=json&lang=${lang}`)}>
              JSON
            </a>
          </div>
        )}
      </div>
      {message && <p className="banner small">{message}</p>}
      <p className="muted small">
        {t("rules.intro")}
        {estimate && data.aiUsable && (
          <>
            {" "}
            <span title={estimate.note}>
              {t("rules.estimate", { requests: estimate.requests, input: Math.round(estimate.inputTokens / 1000), output: Math.round(estimate.outputTokens / 1000), methods: estimate.methods })}
            </span>
          </>
        )}
      </p>

      {latest && (
        <div className="card small">
          <strong>{t("rules.lastAnalysis")}</strong> {formatDate(latest.startedAtUtc)} · {latest.provider}/{latest.model} ·{" "}
          {t(`rules.status.${latest.status}` as "rules.status.Completed")}
          {latest.status !== "Running" && (
            <>
              {" · "}
              {t("rules.stats", { candidates: latest.candidatesFound, sent: latest.snippetsSent, rules: latest.rulesFound })}
              {" · "}
              {t("rules.tokens", { input: latest.inputTokens.toLocaleString(), output: latest.outputTokens.toLocaleString() })}
            </>
          )}
          {latest.error && <div className="error small">{latest.error}</div>}
        </div>
      )}

      {data.rules.length === 0 && !running && <p className="muted">{t("rules.empty")}</p>}

      {data.rules.length > 0 && (
        <>
          <div className="toolbar">
            <input className="search" placeholder={t("rules.filter")} value={filter} onChange={(e) => setFilter(e.target.value)} />
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">{t("rules.allCategories")}</option>
              {categories.map((c) => (
                <option key={c} value={c}>
                  {t(`rules.category.${c}` as "rules.category.Validation")}
                </option>
              ))}
            </select>
            <span className="muted small">{t("rules.count", { shown: rules.length, total: data.rules.length })}</span>
          </div>

          {Object.entries(byFile).map(([file, items]) => (
            <div key={file} className="card">
              <div className="mono small muted">{file}</div>
              <ul className="rules">
                {items.map((r) => (
                  <li key={r.id}>
                    <div className="rule-head">
                      <strong>{r.name}</strong>
                      <span className="tag">{t(`rules.category.${r.category}` as "rules.category.Validation")}</span>
                      <span className="muted small" title={t("rules.confidence")}>
                        {Math.round(r.confidence * 100)}%
                      </span>
                      <span className="mono small muted">
                        {r.symbol}:{r.startLine}
                      </span>
                      <FeedbackBar
                        rating={r.rating}
                        compact
                        onRate={async (rating, comment) => {
                          const updated = await api.rateBusinessRule(assessmentId, r.id, { rating, comment, author: null }, lang);
                          setData((d) => (d ? { ...d, rules: d.rules.map((x) => (x.id === updated.id ? updated : x)) } : d));
                        }}
                      />
                    </div>
                    <p>{r.description}</p>
                    {r.conditions.length > 0 && (
                      <ul className="small conditions">
                        {r.conditions.map((c, i) => (
                          <li key={i}>{c}</li>
                        ))}
                      </ul>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </>
      )}
    </div>
  );
}
