import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, type Job } from "../api";
import { ErrorBox, StatusChip } from "../components";
import { PageHeader, Skeleton, Tile } from "../components/ui";
import { useI18n } from "../i18n";

export function JobsPage() {
  const { t, formatDate } = useI18n();
  const [jobs, setJobs] = useState<Job[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [state, setState] = useState("");
  const [reload, setReload] = useState(0);

  useEffect(() => {
    let alive = true;
    const load = () =>
      api
        .listJobs(state || undefined)
        .then((list) => alive && (setJobs(list), setError(null)))
        .catch(() => alive && setError(t("error.load")));
    load();

    // Live updates via server-sent events; the 5s polling stays as the fallback.
    let source: EventSource | null = null;
    let timer: ReturnType<typeof setInterval> | null = null;
    const startPolling = () => {
      if (!timer) timer = setInterval(load, 5000);
    };
    if (typeof EventSource !== "undefined" && !state) {
      try {
        source = new EventSource(api.jobsEventsUrl());
        source.onmessage = (event) => {
          if (!alive) return;
          try {
            setJobs(JSON.parse(event.data) as Job[]);
            setError(null);
          } catch {
            /* malformed frame: ignore */
          }
        };
        source.onerror = () => {
          source?.close();
          source = null;
          if (alive) startPolling();
        };
      } catch {
        startPolling();
      }
    } else {
      startPolling();
    }

    return () => {
      alive = false;
      source?.close();
      if (timer) clearInterval(timer);
    };
  }, [state, reload, t]);

  async function retry(id: string) {
    try {
      await api.retryJob(id);
      setReload((r) => r + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  const dead = jobs?.filter((j) => j.state === "DeadLetter").length ?? 0;

  return (
    <>
      <PageHeader title={t("jobs.title")} subtitle={t("jobs.subtitle")} />
      {jobs && (
        <div className="kpis">
          {(["Queued", "Running", "Succeeded", "DeadLetter"] as const).map((st) => {
            const n = jobs.filter((j) => j.state === st || (st === "Running" && j.state === "Leased")).length;
            return <Tile key={st} value={n} label={t(`jobs.state.${st}` as "jobs.state.Queued")} tone={st === "DeadLetter" ? (n > 0 ? "critical" : "ok") : st === "Succeeded" ? "ok" : n > 0 ? "accent" : "neutral"} onClick={() => setState(state === st ? "" : st)} />;
          })}
        </div>
      )}
      <div className="filters">
        <label className="filter">
          <span>{t("jobs.state")}</span>
          <select value={state} onChange={(e) => setState(e.target.value)}>
            <option value="">{t("findings.any")}</option>
            {["Queued", "Leased", "Running", "Succeeded", "DeadLetter"].map((s) => (
              <option key={s} value={s}>
                {t(`jobs.state.${s}` as "jobs.state.Queued")}
              </option>
            ))}
          </select>
        </label>
        {dead > 0 && <span className="risk-High">{t("jobs.deadLetters", { count: dead })}</span>}
      </div>
      {error && <ErrorBox message={error} />}
      {!jobs && <Skeleton kind="line" count={5} />}
      {jobs && (
        <section className="card">
          <table>
            <thead>
              <tr>
                <th>{t("jobs.assessment")}</th>
                <th>{t("jobs.kind")}</th>
                <th>{t("jobs.state")}</th>
                <th className="num">{t("jobs.attempt")}</th>
                <th>{t("jobs.queued")}</th>
                <th>{t("jobs.started")}</th>
                <th>{t("jobs.finished")}</th>
                <th>{t("jobs.worker")}</th>
                <th>{t("jobs.error")}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id} className={j.state === "DeadLetter" ? "dead" : ""}>
                  <td>
                    <Link to={`/assessments/${j.assessmentId}`}>{j.assessmentName ?? j.assessmentId}</Link>
                  </td>
                  <td className="small">{t(`jobs.kind.${j.kind}` as "jobs.kind.scan")}</td>
                  <td>
                    <StatusChip status={j.state} />
                  </td>
                  <td className="num">{j.attempt}</td>
                  <td className="small">{formatDate(j.queuedAtUtc)}</td>
                  <td className="small">{formatDate(j.startedAtUtc)}</td>
                  <td className="small">{formatDate(j.finishedAtUtc)}</td>
                  <td className="mono small">{j.leasedBy ?? "—"}</td>
                  <td className="small">{j.error ?? ""}</td>
                  <td>
                    {j.state === "DeadLetter" && (
                      <button className="button small" onClick={() => retry(j.id)}>
                        {t("jobs.retry")}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {jobs.length === 0 && <p className="muted">{t("jobs.empty")}</p>}
        </section>
      )}
    </>
  );
}
