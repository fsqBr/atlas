import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { api, type Finding, type FindingFix, type HeatmapRow, type Narrative, type Paged, type RuleGroup, type SuppressionPolicy } from "../api";
import { ErrorBox, SeverityChip, Spinner } from "../components";
import { FeedbackBar } from "../components/FeedbackBar";
import { Markdown } from "../components/Markdown";
import { StackedHBars, useTokens } from "../components/charts";
import { useI18n } from "../i18n";

const SEVERITIES = ["Critical", "High", "Medium", "Low", "Informational"];
const CATEGORIES = ["Security", "Secrets", "Data", "Modernization", "Dependencies", "Architecture", "Quality"];
const STATUSES = ["Open", "Regressed", "Resolved", "Suppressed", "FalsePositive"];
const PAGE_SIZE = 25;
const AUTHOR_KEY = "atlas.triage.author";

function storedAuthor() {
  try {
    return localStorage.getItem(AUTHOR_KEY) ?? "";
  } catch {
    return "";
  }
}

export function FindingsPanel({ assessmentId, onTriaged }: { assessmentId: string; onTriaged?: () => void }) {
  const { t, term, lang, formatNumber } = useI18n();
  const tk = useTokens();
  const [aiUsable, setAiUsable] = useState(false);
  useEffect(() => {
    api.getAiSettings().then((s) => setAiUsable(s.usable)).catch(() => setAiUsable(false));
  }, []);
  const [page, setPage] = useState(1);
  const [severity, setSeverity] = useState("");
  const [category, setCategory] = useState("");
  const [status, setStatus] = useState("");
  const [search, setSearch] = useState("");
  const [data, setData] = useState<Paged<Finding> | null>(null);
  const [params] = useSearchParams();
  const [expanded, setExpanded] = useState<string | null>(params.get("finding"));
  const [error, setError] = useState<string | null>(null);
  const [policies, setPolicies] = useState<SuppressionPolicy[] | null>(null);
  const [view, setView] = useState<"list" | "rule" | "folder">("list");
  const [groups, setGroups] = useState<RuleGroup[] | null>(null);
  const [heatmap, setHeatmap] = useState<HeatmapRow[] | null>(null);

  const [showPolicies, setShowPolicies] = useState(false);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    api.listPolicies(assessmentId).then(setPolicies).catch(() => setPolicies([]));
  }, [assessmentId, reload]);

  useEffect(() => {
    if (view === "rule") api.findingsByRule(assessmentId, lang).then(setGroups).catch(() => setGroups([]));
    if (view === "folder") api.findingsHeatmap(assessmentId).then(setHeatmap).catch(() => setHeatmap([]));
  }, [view, assessmentId, lang, reload]);

  useEffect(() => {
    const handle = setTimeout(() => {
      api
        .getFindings(assessmentId, { page, pageSize: PAGE_SIZE, severity, category, status, search, lang })
        .then(setData)
        .catch(() => setData({ items: [], page: 1, pageSize: PAGE_SIZE, total: 0 }));
    }, 200);
    return () => clearTimeout(handle);
  }, [assessmentId, page, severity, category, status, search, lang, reload]);

  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  function replace(updated: Finding) {
    setData((d) => (d ? { ...d, items: d.items.map((f) => (f.id === updated.id ? updated : f)) } : d));
    onTriaged?.();
  }

  const select = (value: string, set: (v: string) => void, options: string[], prefix: string, label: string) => (
    <label className="filter">
      <span>{label}</span>
      <select
        value={value}
        onChange={(e) => {
          set(e.target.value);
          setPage(1);
        }}
      >
        <option value="">{t("findings.any")}</option>
        {options.map((o) => (
          <option key={o} value={o}>
            {term(prefix, o)}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <section className="card">
      <div className="filters">
        <input
          className="search"
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setPage(1);
          }}
          placeholder={t("findings.search")}
        />
        {select(severity, setSeverity, SEVERITIES, "sev", t("findings.severity"))}
        {select(category, setCategory, CATEGORIES, "cat", t("findings.category"))}
        {select(status, setStatus, STATUSES, "fstatus", t("findings.status"))}
        <label className="filter">
          <span>{t("findings.view")}</span>
          <select value={view} onChange={(e) => setView(e.target.value as "list" | "rule" | "folder")}>
            <option value="list">{t("findings.view.list")}</option>
            <option value="rule">{t("findings.view.rule")}</option>
            <option value="folder">{t("findings.view.folder")}</option>
          </select>
        </label>
        <span className="muted small total">{data ? t("findings.total", { total: formatNumber(data.total) }) : ""}</span>
        <span className="export-row">
          <span className="muted small">{t("findings.export")}:</span>
          {(["csv", "json", "sarif"] as const).map((format) => (
            <a key={format} className="button small" href={api.exportUrl(assessmentId, format, lang, status || undefined)}>
              {format.toUpperCase()}
            </a>
          ))}
        </span>
      </div>

      <p className="muted small">
        <button type="button" className="button small" onClick={() => setShowPolicies(!showPolicies)}>
          {t("findings.policies")} ({policies?.length ?? 0})
        </button>
      </p>
      {showPolicies && (
        <div className="policies">
          {policies && policies.length === 0 && <p className="muted small">{t("findings.policiesEmpty")}</p>}
          {policies && policies.length > 0 && (
            <table>
              <thead>
                <tr>
                  <th>{t("findings.policyRule")}</th>
                  <th>{t("findings.policyPath")}</th>
                  <th>{t("triage.reason")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {policies.map((p) => (
                  <tr key={p.id}>
                    <td className="mono">
                      {p.rulePattern}
                      {p.assessmentId === null && <span className="tag">{t("findings.policyGlobal")}</span>}
                    </td>
                    <td className="mono">{p.pathGlob ?? "—"}</td>
                    <td className="small">
                      {p.reason} <span className="muted">· {p.author}</span>
                    </td>
                    <td>
                      <button
                        type="button"
                        className="button small danger"
                        onClick={() => api.deletePolicy(p.id).then(() => setReload((r) => r + 1)).catch((e) => setError(String(e)))}
                      >
                        {t("findings.policyDelete")}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {error && <ErrorBox message={error} />}

      {view === "rule" && (
        !groups ? <Spinner /> : (
          <table>
            <thead>
              <tr>
                <th>{t("findings.severity")}</th>
                <th>{t("findings.finding")}</th>
                <th className="num">{t("findings.count")}</th>
                <th>{t("findings.sampleFiles")}</th>
              </tr>
            </thead>
            <tbody>
              {groups.map((g) => (
                <tr key={g.ruleId} onClick={() => { setSearch(""); setSeverity(""); setCategory(""); setView("list"); }} style={{ cursor: "pointer" }}>
                  <td><SeverityChip severity={g.maxSeverity} /></td>
                  <td>
                    <div className="strong">{g.title}</div>
                    <div className="mono small muted">{g.ruleId} · {term("cat", g.category)}</div>
                  </td>
                  <td className="num">{formatNumber(g.count)}</td>
                  <td className="mono small">{g.sampleFiles.map((f) => <div key={f}>{f}</div>)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )
      )}

      {view === "folder" && (
        !heatmap ? <Spinner /> : (
          <>
          <p className="eyebrow">{t("findings.heatChart")}</p>
          <StackedHBars
            height={heatmap.length > 8 ? "h-lg" : "h-md"}
            labelWidth={160}
            data={[...heatmap].sort((a, b) => b.open - a.open).slice(0, 12).map((r) => ({ name: r.folder, critical: r.critical, high: r.high, medium: r.medium, low: r.low + r.informational }))}
            keys={[
              { key: "critical", label: term("sev", "Critical"), color: tk.critical },
              { key: "high", label: term("sev", "High"), color: tk.high },
              { key: "medium", label: term("sev", "Medium"), color: tk.medium },
              { key: "low", label: term("sev", "Low"), color: tk.low },
            ]}
            emptyText={t("findings.empty")}
          />
          <table>
            <thead>
              <tr>
                <th>{t("findings.folder")}</th>
                <th className="num">{t("findings.files")}</th>
                <th className="num">{t("findings.count")}</th>
                <th>{t("findings.severity")}</th>
              </tr>
            </thead>
            <tbody>
              {heatmap.map((row) => {
                const max = Math.max(1, ...heatmap.map((r) => r.open));
                return (
                  <tr key={row.folder}>
                    <td className="mono">{row.folder}</td>
                    <td className="num">{row.files}</td>
                    <td className="num">{formatNumber(row.open)}</td>
                    <td>
                      <div className="heat" title={`${row.critical} / ${row.high} / ${row.medium} / ${row.low}`}>
                        <span className="heat-seg sev-critical" style={{ width: `${(row.critical / max) * 100}%` }} />
                        <span className="heat-seg sev-high" style={{ width: `${(row.high / max) * 100}%` }} />
                        <span className="heat-seg sev-medium" style={{ width: `${(row.medium / max) * 100}%` }} />
                        <span className="heat-seg sev-low" style={{ width: `${((row.low + row.informational) / max) * 100}%` }} />
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          </>
        )
      )}

      {view === "list" && !data && <Spinner />}
      {view === "list" && data && data.items.length === 0 && <p className="muted empty">{t("findings.empty")}</p>}

      {view === "list" && data && data.items.length > 0 && (
        <table className="findings">
          <thead>
            <tr>
              <th>{t("findings.severity")}</th>
              <th>{t("findings.finding")}</th>
              <th>{t("findings.location")}</th>
              <th>{t("findings.status")}</th>
              <th>{t("findings.confidence")}</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((f) => (
              <tr key={f.id} className={`${expanded === f.id ? "expanded" : ""} ${f.status === "Suppressed" || f.status === "FalsePositive" ? "triaged" : ""}`}>
                <td onClick={() => setExpanded(expanded === f.id ? null : f.id)}>
                  <SeverityChip severity={f.severity} />
                </td>
                <td onClick={() => setExpanded(expanded === f.id ? null : f.id)}>
                  <div className="strong">{f.title}</div>
                  <div className="mono small muted">
                    {f.ruleId} · {term("cat", f.category)}
                  </div>
                  {expanded === f.id && (
                    <div className="details" onClick={(e) => e.stopPropagation()}>
                      {f.message && <p>{f.message}</p>}
                      {f.remediation && (
                        <p>
                          <strong>{t("findings.remediation")}:</strong> {f.remediation}
                        </p>
                      )}
                      {f.suppression && (
                        <p className="triage-note">
                          <strong>{term("fstatus", f.suppression.kind)}</strong> · {f.suppression.author} · {f.suppression.reason}
                        </p>
                      )}
                      <TriageBar assessmentId={assessmentId} finding={f} aiUsable={aiUsable} onDone={replace} onError={setError} onPolicy={() => setReload((r) => r + 1)} />
                    </div>
                  )}
                </td>
                <td className="mono small">
                  {f.filePath ?? f.symbol ?? "—"}
                  {f.lineStart ? `:${f.lineStart}` : ""}
                </td>
                <td>{term("fstatus", f.status)}</td>
                <td className="muted">{f.confidence ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {view === "list" && data && pages > 1 && (
        <div className="pager">
          <button className="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            {t("findings.prev")}
          </button>
          <span className="muted small">{t("findings.page", { page, pages })}</span>
          <button className="button" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            {t("findings.next")}
          </button>
        </div>
      )}
    </section>
  );
}

function TriageBar({
  assessmentId,
  finding,
  aiUsable,
  onDone,
  onError,
  onPolicy,
}: {
  assessmentId: string;
  finding: Finding;
  aiUsable: boolean;
  onDone: (f: Finding) => void;
  onError: (message: string | null) => void;
  onPolicy?: () => void;
}) {
  const { t, lang, formatDate } = useI18n();
  const [policyBusy, setPolicyBusy] = useState(false);

  async function createPolicy(withPath: boolean) {
    const reason = window.prompt(t("triage.policyReason"));
    if (!reason) return;
    const who = author || window.prompt(t("triage.author")) || "";
    if (!who) return;
    const folder = finding.filePath ? finding.filePath.replace(/\\/g, "/").split("/").slice(0, -1).join("/") : "";
    setPolicyBusy(true);
    onError(null);
    try {
      const result = await api.createPolicy(assessmentId, {
        rulePattern: finding.ruleId,
        pathGlob: withPath && folder ? `${folder}/` : null,
        reason,
        author: who,
      });
      onError(t("triage.policyCreated", { applied: result.appliedToExisting }));
      onPolicy?.();
    } catch (err) {
      onError(`${t("triage.error")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setPolicyBusy(false);
    }
  }
  const [mode, setMode] = useState<"Suppress" | "FalsePositive" | null>(null);
  const [explanation, setExplanation] = useState<Narrative | null>(null);
  const [explaining, setExplaining] = useState(false);

  useEffect(() => {
    if (!aiUsable) return;
    let alive = true;
    api.getExplanation(assessmentId, finding.id, lang).then((e) => alive && e && setExplanation(e)).catch(() => {});
    return () => {
      alive = false;
    };
  }, [aiUsable, assessmentId, finding.id, lang]);

  async function explain(refresh: boolean) {
    setExplaining(true);
    onError(null);
    try {
      setExplanation(await api.explainFinding(assessmentId, finding.id, lang, refresh));
    } catch (err) {
      onError(`${t("ai.explainError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setExplaining(false);
    }
  }

  const fixEligible = !!finding.filePath && finding.filePath !== "estate" && finding.category !== "Secrets" && !finding.ruleId.startsWith("secrets.");
  const [fix, setFix] = useState<FindingFix | null>(null);
  const [fixBusy, setFixBusy] = useState(false);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!aiUsable || !fixEligible) return;
    let alive = true;
    api.getFix(assessmentId, finding.id, lang).then((f) => alive && setFix(f)).catch(() => {});
    return () => {
      alive = false;
    };
  }, [aiUsable, fixEligible, assessmentId, finding.id, lang]);

  async function requestFix() {
    setFixBusy(true);
    onError(null);
    try {
      await api.requestFix(assessmentId, finding.id, lang);
      for (let attempt = 0; attempt < 60; attempt++) {
        await new Promise((r) => setTimeout(r, 3000));
        const current = await api.getFix(assessmentId, finding.id, lang);
        setFix(current);
        if (current.jobState === "Succeeded" || current.jobState === "DeadLetter") break;
      }
    } catch (err) {
      onError(`${t("ai.fixError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setFixBusy(false);
    }
  }

  function copyDiff() {
    const text = fix?.fix?.text ?? "";
    const m = /```(?:diff|patch)\s*\n([\s\S]*?)```/.exec(text);
    void navigator.clipboard?.writeText(m ? m[1] : text).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  }

  const fixBlock = aiUsable && fixEligible && (
    <>
      <button className="button small" disabled={fixBusy} onClick={requestFix} title={t("ai.fixHint")}>
        {fixBusy ? t("ai.fixing") : fix?.fix ? `🩹 ${t("ai.fixRefresh")}` : `🩹 ${t("ai.fix")}`}
      </button>
      {fix?.jobState === "DeadLetter" && !fixBusy && <span className="risk-High small">{t("ai.fixFailed", { error: fix.jobError ?? "" })}</span>}
      {fix?.fix && (
        <div className="ai-box ai-plan">
          <Markdown text={fix.fix.text} headingOffset={2} />
          <div className="row">
            <small className="muted">{t("ai.explainLabel", { model: fix.fix.model, when: formatDate(fix.fix.createdAtUtc), cached: "" })}</small>
            <button type="button" className="button small" onClick={copyDiff}>
              {copied ? t("ai.fixCopied") : t("ai.fixCopy")}
            </button>
            <FeedbackBar
              rating={fix.fix.rating}
              compact
              onRate={async (rating, comment) => {
                const updated = await api.rateNarrative(assessmentId, "finding-fix", { rating, comment, author: storedAuthor() || null }, lang, finding.id);
                setFix((f) => (f ? { ...f, fix: updated } : f));
              }}
            />
          </div>
        </div>
      )}
    </>
  );

  const explainBlock = (
    <>
      {aiUsable && (
        <button className="button small" disabled={explaining} onClick={() => explain(!!explanation)}>
          {explaining ? t("ai.explaining") : explanation ? `✨ ${t("ai.explainRefresh")}` : `✨ ${t("ai.explain")}`}
        </button>
      )}
      {fixBlock}
      {explanation && (
        <div className="ai-box">
          {explanation.text.split(/\n+/).map((p, i) => (
            <p key={i}>{p}</p>
          ))}
          <div className="row">
            <small className="muted">
              {t("ai.explainLabel", { model: explanation.model, when: formatDate(explanation.createdAtUtc), cached: explanation.cached ? t("ai.cachedSuffix") : "" })}
            </small>
            <FeedbackBar
              rating={explanation.rating}
              compact
              onRate={async (rating, comment) => setExplanation(await api.rateNarrative(assessmentId, "finding-explanation", { rating, comment, author: storedAuthor() || null }, lang, finding.id))}
            />
          </div>
        </div>
      )}
    </>
  );
  const [reason, setReason] = useState("");
  const [author, setAuthor] = useState(storedAuthor);
  const [busy, setBusy] = useState(false);
  const triaged = finding.status === "Suppressed" || finding.status === "FalsePositive";

  async function submit(action: "Suppress" | "FalsePositive" | "Reopen") {
    setBusy(true);
    onError(null);
    try {
      try {
        localStorage.setItem(AUTHOR_KEY, author);
      } catch {
        /* ignore */
      }
      const updated = await api.triage(assessmentId, finding.id, { action, reason: action === "Reopen" ? null : reason, author }, lang);
      onDone(updated);
      setMode(null);
      setReason("");
    } catch (err) {
      onError(`${t("triage.error")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setBusy(false);
    }
  }

  if (triaged) {
    return (
      <div className="triage">
        <button className="button" disabled={busy} onClick={() => submit("Reopen")}>
          {t("triage.reopen")}
        </button>
        {explainBlock}
      </div>
    );
  }

  return (
    <div className="triage">
      {mode === null ? (
        <>
          <button className="button" onClick={() => setMode("Suppress")}>
            {t("triage.suppress")}
          </button>
          <button className="button" onClick={() => setMode("FalsePositive")}>
            {t("triage.falsePositive")}
          </button>
          <button className="button small" disabled={policyBusy} onClick={() => createPolicy(false)} title={finding.ruleId}>
            {t("triage.policyRule")}
          </button>
          {finding.filePath && (
            <button className="button small" disabled={policyBusy} onClick={() => createPolicy(true)}>
              {t("triage.policyPath")}
            </button>
          )}
          {explainBlock}
        </>
      ) : (
        <form
          className="triage-form"
          onSubmit={(e) => {
            e.preventDefault();
            submit(mode);
          }}
        >
          <strong>{mode === "Suppress" ? t("triage.suppress") : t("triage.falsePositive")}</strong>
          <input value={author} onChange={(e) => setAuthor(e.target.value)} placeholder={t("triage.author")} required />
          <input value={reason} onChange={(e) => setReason(e.target.value)} placeholder={t("triage.reason")} required className="grow" />
          <button type="submit" className="button primary" disabled={busy || !reason.trim() || !author.trim()}>
            {t("triage.confirm")}
          </button>
          <button type="button" className="button" onClick={() => setMode(null)}>
            {t("triage.cancel")}
          </button>
        </form>
      )}
    </div>
  );
}
