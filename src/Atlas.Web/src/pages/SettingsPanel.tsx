import { useState } from "react";
import { api, type Assessment, type Health } from "../api";
import { isAuthEnabled } from "../auth";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";
import { AccessCard } from "./AccessCard";

function targetStatus(score: number | null | undefined, target: number, due: string | null): string {
  if (score != null && score >= target) return "Met";
  if (due) {
    // Same semantics as the server (Targets.Evaluate): the whole target day counts, in UTC.
    const d = new Date(due);
    const endOfDueDay = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 1);
    const now = Date.now();
    if (now >= endOfDueDay) return "Missed";
    if (endOfDueDay - now <= 30 * 86400000) return "AtRisk";
  }
  return score != null && target - score > 20 ? "AtRisk" : "OnTrack";
}

type ScheduleDraft = { days: string; webhook: string; target: string; targetDate: string };

/** The operational side of an assessment — scope exclusions, sharing, schedule and targets — kept out of the overview. */
export function SettingsPanel({ assessment, health, onChanged, onError }: { assessment: Assessment; health: Health | null; onChanged: () => Promise<void>; onError: (message: string) => void }) {
  const { t } = useI18n();
  const [scopeText, setScopeText] = useState<string | null>(null);
  const [scopeMessage, setScopeMessage] = useState<string | null>(null);
  const [schedule, setSchedule] = useState<ScheduleDraft | null>(null);
  const [scheduleMessage, setScheduleMessage] = useState<string | null>(null);
  const [tagsText, setTagsText] = useState<string | null>(null);
  const [tagsMessage, setTagsMessage] = useState<string | null>(null);
  const [sarifMessage, setSarifMessage] = useState<string | null>(null);

  async function saveTags() {
    if (tagsText === null) return;
    try {
      const tags = tagsText.split(",").map((v) => v.trim()).filter((v) => v.length > 0);
      await api.setTags(assessment.id, tags);
      setTagsMessage(t("tags.saved"));
      setTagsText(null);
      await onChanged();
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
    }
  }

  async function importSarif(file: File | undefined) {
    if (!file) return;
    setSarifMessage(null);
    try {
      const result = await api.importSarif(assessment.id, await file.text());
      setSarifMessage(t("sarif.done", { tool: result.tool, imported: result.imported, newFindings: result.newFindings, resolved: result.resolved }));
      await onChanged();
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
    }
  }

  const draft = (): ScheduleDraft =>
    schedule ?? {
      days: assessment.rerunEveryDays?.toString() ?? "",
      webhook: assessment.webhookUrl ?? "",
      target: assessment.targetScore?.toString() ?? "",
      targetDate: assessment.targetDate ? assessment.targetDate.slice(0, 10) : "",
    };

  async function saveSchedule() {
    if (!schedule) return;
    try {
      await api.setSchedule(assessment.id, {
        rerunEveryDays: schedule.days ? Number(schedule.days) : null,
        webhookUrl: schedule.webhook.trim() || null,
        targetScore: schedule.target ? Number(schedule.target) : null,
        targetDate: schedule.target && schedule.targetDate ? schedule.targetDate + "T00:00:00Z" : null,
      });
      setScheduleMessage(t("detail.scheduleSaved"));
      setSchedule(null);
      await onChanged();
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
    }
  }

  async function saveScope() {
    if (scopeText === null) return;
    const paths = scopeText.split("\n").map((l) => l.trim()).filter((l) => l.length > 0 && !l.startsWith("#"));
    try {
      await api.setScope(assessment.id, paths);
      setScopeMessage(t("detail.scopeSaved"));
      setScopeText(null);
      await onChanged();
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <>
      <p className="muted" style={{ margin: "0 0 1rem" }}>{t("settings.intro")}</p>
      <div className="grid-2">
        <Card title={t("detail.scope")} subtitle={t("detail.scopeHint")}>
          <textarea
            className="mono"
            rows={5}
            style={{ width: "100%" }}
            value={scopeText ?? assessment.excludePaths.join("\n")}
            onChange={(e) => {
              setScopeText(e.target.value);
              setScopeMessage(null);
            }}
            placeholder={"legacy-copy/\n**/*.generated.cs"}
          />
          <div className="actions" style={{ marginTop: "0.6rem" }}>
            <button className="button primary" onClick={saveScope} disabled={scopeText === null}>
              {t("detail.scopeSave")}
            </button>
            {scopeMessage && <span className="muted small">{scopeMessage}</span>}
          </div>
        </Card>

        <Card title={t("detail.schedule")} subtitle={t("detail.scheduleHint")}>
          <div className="form" style={{ maxWidth: "none" }}>
            <div className="discover-row">
              <label>
                <span>{t("detail.rerunEvery")}</span>
                <select value={draft().days} onChange={(e) => setSchedule({ ...draft(), days: e.target.value })}>
                  <option value="">{t("detail.rerunManual")}</option>
                  {[1, 7, 14, 30, 90].map((d) => (
                    <option key={d} value={d}>{d}</option>
                  ))}
                </select>
              </label>
              <label style={{ flex: 1 }}>
                <span>{t("detail.webhook")}</span>
                <input className="mono" value={draft().webhook} onChange={(e) => setSchedule({ ...draft(), webhook: e.target.value })} placeholder="https://hooks.example.com/atlas" />
              </label>
            </div>
            <div className="discover-row">
              <label>
                <span>{t("detail.target")}</span>
                <input type="number" min={1} max={100} style={{ maxWidth: "6rem" }} value={draft().target} onChange={(e) => setSchedule({ ...draft(), target: e.target.value })} placeholder={t("detail.targetNone")} />
              </label>
              <label>
                <span>{t("detail.targetBy")}</span>
                <input type="date" value={draft().targetDate} onChange={(e) => setSchedule({ ...draft(), targetDate: e.target.value })} />
              </label>
            </div>
            <p className="muted small" style={{ margin: 0 }}>
              {t("detail.targetHint")}
              {health && assessment.targetScore && (
                <> <strong className={`target-${targetStatus(health.score, assessment.targetScore, assessment.targetDate)}`}>{t(`target.${targetStatus(health.score, assessment.targetScore, assessment.targetDate)}` as "target.Met")}</strong></>
              )}
            </p>
            <div className="actions">
              <button className="button primary" onClick={saveSchedule} disabled={schedule === null}>
                {t("detail.scheduleSave")}
              </button>
              {scheduleMessage && <span className="muted small">{scheduleMessage}</span>}
            </div>
          </div>
        </Card>
      </div>
      <div className="grid-2">
        <Card title={t("tags.title")} subtitle={t("tags.hint")}>
          <input
            style={{ width: "100%" }}
            value={tagsText ?? (assessment.tags ?? []).join(", ")}
            onChange={(e) => {
              setTagsText(e.target.value);
              setTagsMessage(null);
            }}
            placeholder="billing, client-x, core"
          />
          <div className="actions" style={{ marginTop: "0.6rem" }}>
            <button className="button primary" onClick={saveTags} disabled={tagsText === null}>
              {t("tags.save")}
            </button>
            {tagsMessage && <span className="muted small">{tagsMessage}</span>}
          </div>
        </Card>

        <Card title={t("sarif.title")} subtitle={t("sarif.hint")}>
          <input type="file" accept=".sarif,.json,application/json" onChange={(e) => importSarif(e.target.files?.[0])} />
          {sarifMessage && <p className="muted small" style={{ marginTop: "0.6rem" }}>{sarifMessage}</p>}
        </Card>
      </div>
      <AccessCard assessmentId={assessment.id} authEnabled={isAuthEnabled()} />
    </>
  );
}
