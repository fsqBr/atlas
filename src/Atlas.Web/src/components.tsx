import type { Health } from "./api";
import { useI18n } from "./i18n";

export function SeverityChip({ severity }: { severity: string }) {
  const { term } = useI18n();
  return <span className={`chip sev-${severity.toLowerCase()}`}>{term("sev", severity)}</span>;
}

export function StatusChip({ status }: { status: string }) {
  const { term } = useI18n();
  return <span className={`chip st-${status.toLowerCase()}`}>{term("status", status)}</span>;
}

export function RiskBadge({ level }: { level: string | null | undefined }) {
  const { term } = useI18n();
  if (!level) return <span className="muted">—</span>;
  return <span className={`risk risk-${level.toLowerCase()}`}>{term("risk", level)}</span>;
}

export function ScoreBadge({ score, level }: { score: number | null | undefined; level: string | null | undefined }) {
  if (score === null || score === undefined) return <span className="muted">—</span>;
  return (
    <span className={`score-badge risk-${(level ?? "low").toLowerCase()}`}>
      <b>{score}</b>
      <small>/100</small>
    </span>
  );
}

function tone(score: number) {
  return score < 40 ? "critical" : score < 60 ? "high" : score < 80 ? "medium" : "low";
}

export function HealthCard({ health }: { health: Health | null }) {
  const { t, term, formatNumber } = useI18n();

  if (!health) {
    return (
      <section className="card">
        <h2>{t("health.title")}</h2>
        <p className="muted">{t("health.none")}</p>
      </section>
    );
  }

  return (
    <section className="card">
      <h2>{t("health.title")}</h2>
      <div className="health-hero">
        <div className={`score risk-${health.riskLevel.toLowerCase()}`}>
          <span className="score-v">{health.score}</span>
          <span className="score-d">/100</span>
        </div>
        <div>
          <p className="risk-label">
            {t("health.risk")}: <RiskBadge level={health.riskLevel} />
          </p>
          <p className="muted small">
            {formatNumber(health.openFindings)} {t("health.open")} · {formatNumber(health.projectCount)} {t("health.projects")} ·{" "}
            <span className="mono">{health.modelVersion}</span>
          </p>
        </div>
      </div>
      <table>
        <thead>
          <tr>
            <th>{t("health.dimension")}</th>
            <th className="num">{t("health.weight")}</th>
            <th className="num">{t("health.score")}</th>
            <th></th>
            <th className="num">{t("health.penalty")}</th>
            <th>{t("health.contributors")}</th>
          </tr>
        </thead>
        <tbody>
          {health.dimensions.map((d) => (
            <tr key={d.name}>
              <td>
                <strong>{term("dim", d.name)}</strong>
              </td>
              <td className="num">{Math.round(d.weight * 100)}%</td>
              <td className="num">{d.score}</td>
              <td>
                <div className="bar">
                  <div className={`bar-fill bar-${tone(d.score)}`} style={{ width: `${d.score}%` }} />
                </div>
              </td>
              <td className="num">−{d.penalty}</td>
              <td className="small">
                {d.contributors.length === 0 ? (
                  <span className="muted">—</span>
                ) : (
                  d.contributors.map((c) => (
                    <div key={c.ruleId} className="mono">
                      {c.ruleId} ×{c.count} <span className="muted">(−{c.points.toFixed(1)})</span>
                    </div>
                  ))
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

export function ErrorBox({ message }: { message: string }) {
  return (
    <div className="error" role="alert">
      {message}
    </div>
  );
}

export function Spinner({ label }: { label?: string }) {
  return (
    <div className="spinner" role="status">
      <span className="dot" />
      {label && <span>{label}</span>}
    </div>
  );
}
