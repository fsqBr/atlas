import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { pickFolder, supportsDirectoryPicker, zipAndUpload } from "../upload";
import { api, type Assessment, type Health } from "../api";
import { ErrorBox, Spinner, StatusChip } from "../components";
import { useI18n } from "../i18n";
import { RunsPanel } from "./RunsPanel";
import { BusinessRulesPanel } from "./BusinessRulesPanel";
import { FindingsPanel } from "./FindingsPanel";
import { ModernizationPanel } from "./ModernizationPanel";
import { OverviewPanel } from "./OverviewPanel";
import { SettingsPanel } from "./SettingsPanel";

type Tab = "overview" | "findings" | "runs" | "modernization" | "rules" | "report" | "settings";

function isActive(status: string) {
  return status === "Created" || status === "Running";
}

export function AssessmentDetailPage() {
  const { id = "" } = useParams();
  const { t, lang, formatDate } = useI18n();
  const navigate = useNavigate();

  const [assessment, setAssessment] = useState<Assessment | null>(null);
  const [health, setHealth] = useState<Health | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [params, setParams] = useSearchParams();
  const TABS: Tab[] = ["overview", "findings", "runs", "modernization", "rules", "report", "settings"];
  const paramTab = params.get("tab");
  const tab: Tab = TABS.includes(paramTab as Tab) ? (paramTab as Tab) : "overview";
  const setTab = (next: Tab) => {
    const q = new URLSearchParams(params);
    if (next === "overview") q.delete("tab");
    else q.set("tab", next);
    setParams(q, { replace: true });
  };
  const [queuing, setQueuing] = useState(false);
  const [justQueued, setJustQueued] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [reuploadStatus, setReuploadStatus] = useState<string | null>(null);
  const [aiUsable, setAiUsable] = useState(false);
  const [summaryBusy, setSummaryBusy] = useState(false);
  const [summaryMessage, setSummaryMessage] = useState<string | null>(null);
  const [hasSummary, setHasSummary] = useState(false);
  const [reportNonce, setReportNonce] = useState(0);

  useEffect(() => {
    api.getAiSettings().then((s) => setAiUsable(s.usable)).catch(() => setAiUsable(false));
  }, []);

  useEffect(() => {
    if (!id) return;
    api.getSummary(id, lang).then((s) => setHasSummary(!!s)).catch(() => setHasSummary(false));
  }, [id, lang, reportNonce]);

  async function writeSummary() {
    setSummaryBusy(true);
    setSummaryMessage(null);
    try {
      const r = await api.generateSummary(id, lang);
      setSummaryMessage(t("ai.summaryDone", { model: r.model }));
      setReportNonce((n) => n + 1);
    } catch (err) {
      setSummaryMessage(`${t("ai.summaryError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setSummaryBusy(false);
    }
  }

  async function reupload() {
    if (!id) return;
    setRunError(null);
    try {
      const picked = await pickFolder((p) => setReuploadStatus(`${p.files}`));
      setReuploadStatus("…");
      const uploaded = await zipAndUpload(picked, (p) => setReuploadStatus(p.phase === "zipping" ? `${p.percent ?? 0}%` : `${(p.bytes / 1048576).toFixed(1)} MB`));
      await api.replaceUpload(id, uploaded.uploadId);
      await load();
    } catch (err) {
      if ((err as { name?: string }).name !== "AbortError") {
        setRunError(`${t("detail.reuploadError")}: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setReuploadStatus(null);
    }
  }

  async function runAgain() {
    setQueuing(true);
    setRunError(null);
    try {
      await api.runAgain(id);
      setJustQueued(true); // immediate feedback; the server's activeJobState takes over on the next poll
      await load();
    } catch (err) {
      setRunError(`${t("detail.runAgainError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setQueuing(false);
    }
  }

  async function rename() {
    if (!assessment) return;
    const name = window.prompt(t("detail.renamePrompt"), assessment.name);
    if (!name || name.trim() === assessment.name) return;
    try {
      await api.renameAssessment(id, name.trim());
      await load();
    } catch (err) {
      setRunError(err instanceof Error ? err.message : String(err));
    }
  }

  async function remove() {
    if (!window.confirm(t("detail.deleteConfirm"))) return;
    try {
      await api.deleteAssessment(id);
      navigate("/");
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setRunError(message.includes("queued or running") ? t("detail.deleteBusy") : `${t("detail.deleteError")}: ${message}`);
    }
  }

  const load = useCallback(async () => {
    try {
      const a = await api.getAssessment(id);
      setAssessment(a);
      setError(null);
      if (a.activeJobState) setJustQueued(false); // server now reports the pending job itself
      if (!isActive(a.status) && !a.activeJobState) setHealth(await api.getHealth(id));
    } catch {
      setError(t("error.load"));
    }
  }, [id, t]);

  useEffect(() => {
    load();
  }, [load]);

  const active = !!assessment && (isActive(assessment.status) || !!assessment.activeJobState || justQueued);

  useEffect(() => {
    if (!active) return;
    const timer = setInterval(load, 3000);
    return () => clearInterval(timer);
  }, [active, load]);

  if (error) return <ErrorBox message={error} />;
  if (!assessment) return <Spinner />;

  const pendingLabel = justQueued || assessment.activeJobState === "Queued"
    ? t("detail.rerunQueued")
    : assessment.activeJobState
      ? t("detail.rerunRunning")
      : assessment.status === "Created"
        ? t("detail.queued")
        : t("detail.running");

  return (
    <>
      <p className="crumb">
        <Link to="/assessments">← {t("detail.back")}</Link>
      </p>
      <div className="page-head">
        <div>
          <h1>{assessment.name}</h1>
          <p className="muted small">
            <span className="kind">{assessment.sourceKind}</span> <span className="mono">{assessment.sourceLocator}</span>
            {assessment.branch && (
              <>
                {" "}
                · {t("detail.branch")} <span className="mono">{assessment.branch}</span>
              </>
            )}
            {" · "}
            {t("detail.created")} {formatDate(assessment.createdAtUtc)}
            {assessment.completedAtUtc && (
              <>
                {" · "}
                {t("detail.completed")} {formatDate(assessment.completedAtUtc)}
              </>
            )}
          </p>
        </div>
        <div className="head-actions">
          <StatusChip status={assessment.status} />
          <button className="button primary" onClick={runAgain} disabled={active || queuing}>
            {queuing ? t("detail.runAgainBusy") : `↻ ${t("detail.runAgain")}`}
          </button>
          {assessment.sourceKind === "upload" && supportsDirectoryPicker() && (
            <button className="button" onClick={reupload} disabled={active || reuploadStatus !== null} title={t("detail.reuploadHint")}>
              {reuploadStatus ? t("detail.reuploadBusy", { status: reuploadStatus }) : `⬆ ${t("detail.reupload")}`}
            </button>
          )}
          <button className="button" onClick={rename} title={t("detail.rename")}>
            ✎ {t("detail.rename")}
          </button>
          <button className="button" onClick={() => window.dispatchEvent(new CustomEvent("atlas-present"))} title={t("present.enterHint")}>
            ▶ {t("present.enter")}
          </button>
          <button className="button danger" onClick={remove} disabled={active} title={active ? t("detail.deleteBusy") : t("detail.delete")}>
            🗑 {t("detail.delete")}
          </button>
        </div>
      </div>

      {active && (
        <div className="banner">
          <Spinner label={pendingLabel} />
        </div>
      )}
      {assessment.failureReason && <ErrorBox message={`${t("detail.failure")}: ${assessment.failureReason}`} />}
      {runError && <ErrorBox message={runError} />}

      <div className="tabs" role="tablist">
        {TABS.map((k) => (
          <button key={k} role="tab" aria-selected={tab === k} className={tab === k ? "tab active" : "tab"} onClick={() => setTab(k)}>
            {t(`detail.tab.${k}` as "detail.tab.overview")}
          </button>
        ))}
      </div>

      {tab === "overview" && <OverviewPanel assessment={assessment} health={health} active={active} onGoTo={setTab} />}
      {tab === "settings" && <SettingsPanel assessment={assessment} health={health} onChanged={load} onError={setRunError} />}

      {tab === "findings" && <FindingsPanel assessmentId={id} onTriaged={() => api.getHealth(id).then(setHealth)} />}
      {tab === "rules" && <BusinessRulesPanel assessmentId={id} active={active} />}

      {tab === "runs" && (
        <RunsPanel assessmentId={id} active={active} refreshKey={`${assessment.status}:${assessment.completedAtUtc ?? ""}:${assessment.activeJobState ?? ""}`} />
      )}

      {tab === "modernization" && <ModernizationPanel assessmentId={id} refreshKey={assessment.completedAtUtc ?? ""} />}

      {tab === "report" && (
        <section className="card report">
          <div className="actions right">
            {aiUsable && (
              <button className="button" onClick={writeSummary} disabled={summaryBusy || active} title={t("ai.summaryHint")}>
                {summaryBusy ? t("ai.summaryGenerating") : `✨ ${hasSummary ? t("ai.summaryRegenerate") : t("ai.summaryGenerate")}`}
              </button>
            )}
            <a className="button" href={api.reportUrl(id, lang)} target="_blank" rel="noreferrer">
              {t("detail.openReport")} ↗
            </a>
            <a className="button primary" href={api.reportPdfUrl(id, lang)}>
              ⬇ {t("detail.downloadPdf")}
            </a>
            <a className="button" href={api.sbomUrl(id)} title={t("detail.sbomHint")}>
              ⬇ SBOM
            </a>
          </div>
          {summaryMessage && <p className="banner small">{summaryMessage}</p>}
          <iframe key={reportNonce} title="report" src={api.reportUrl(id, lang)} className="report-frame" />
        </section>
      )}
    </>
  );
}
