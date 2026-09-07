import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, type AssessmentSummary } from "../api";
import { ErrorBox, ScoreBadge, StatusChip } from "../components";
import { PageHeader, Skeleton } from "../components/ui";
import { useI18n } from "../i18n";

export function AssessmentsPage() {
  const { t, formatDate, formatNumber } = useI18n();
  const [items, setItems] = useState<AssessmentSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const load = () =>
      api
        .listAssessments()
        .then((data) => alive && (setItems(data), setError(null)))
        .catch(() => alive && setError(t("error.load")));
    load();
    const timer = setInterval(load, 5000);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, [t]);

  return (
    <>
      <PageHeader title={t("list.title")} subtitle={t("list.subtitle")} actions={<Link to="/new" className="button primary">＋ {t("nav.new")}</Link>} />

      {error && <ErrorBox message={error} />}
      {!items && !error && <Skeleton kind="line" count={6} />}

      {items && items.length === 0 && <p className="muted empty">{t("list.empty")}</p>}

      {items && items.length > 0 && (
        <section className="card">
          <table className="list">
            <thead>
              <tr>
                <th>{t("list.name")}</th>
                <th>{t("list.source")}</th>
                <th>{t("list.status")}</th>
                <th className="num">{t("list.score")}</th>
                <th className="num">{t("list.findings")}</th>
                <th>{t("list.created")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {items.map((a) => (
                <tr key={a.id}>
                  <td>
                    <Link to={`/assessments/${a.id}`} className="strong">
                      {a.name}
                    </Link>
                  </td>
                  <td className="mono small">
                    <span className="kind">{a.sourceKind}</span> {a.sourceLocator}
                  </td>
                  <td>
                    <StatusChip status={a.status} />
                    {a.activeJobState && (
                      <span className="pending">
                        <span className="dot" /> {a.activeJobState === "Queued" ? t("list.queued") : t("list.running")}
                      </span>
                    )}
                  </td>
                  <td className="num">
                    <ScoreBadge score={a.healthScore} level={a.riskLevel} />
                  </td>
                  <td className="num">{a.openFindings === null ? "—" : formatNumber(a.openFindings)}</td>
                  <td className="small muted">{formatDate(a.createdAtUtc)}</td>
                  <td>
                    <Link to={`/assessments/${a.id}`} className="button">
                      {t("list.open")}
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </>
  );
}
