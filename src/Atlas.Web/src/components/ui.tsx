import type { ReactNode } from "react";
import { Link, useInRouterContext, useLocation } from "react-router-dom";
import { helpSectionForRoute } from "../help/content";
import { useI18n } from "../i18n";

/** Page title block: crumb (optional), title + subtitle on the left, actions and the contextual help button on the right. */
export function PageHeader({ title, subtitle, crumb, actions }: { title: ReactNode; subtitle?: ReactNode; crumb?: ReactNode; actions?: ReactNode }) {
  const inRouter = useInRouterContext();
  return (
    <>
      {crumb && <p className="crumb">{crumb}</p>}
      <div className="page-head">
        <div>
          <h1>{title}</h1>
          {subtitle && <p className="sub">{subtitle}</p>}
        </div>
        {(actions || inRouter) && (
          <div className="head-actions">
            {actions}
            {inRouter && <HelpButton />}
          </div>
        )}
      </div>
    </>
  );
}

/** The `?` in every page header: opens the manual at the section that describes the current route (and tab). */
function HelpButton() {
  const { t } = useI18n();
  const location = useLocation();
  if (location.pathname === "/help") return null;
  const section = helpSectionForRoute(location.pathname, location.search);
  if (!section) return null;
  return (
    <Link to={`/help#${section}`} className="help-btn" title={t("help.forPage")} aria-label={t("help.forPage")} data-testid="help-button">
      ?
    </Link>
  );
}

/** Surface with an optional head row (title, subtitle, actions). Use `className` for grid placement. */
export function Card({ title, subtitle, actions, className, children }: { title?: ReactNode; subtitle?: ReactNode; actions?: ReactNode; className?: string; children: ReactNode }) {
  return (
    <section className={`card${className ? ` ${className}` : ""}`}>
      {(title || actions) && (
        <div className="card-head">
          {title && <h2>{title}</h2>}
          {actions && <div className="actions">{actions}</div>}
          {subtitle && <p className="sub">{subtitle}</p>}
        </div>
      )}
      {children}
    </section>
  );
}

export type Tone = "critical" | "high" | "medium" | "low" | "ok" | "accent" | "neutral";

/** Score → tone, same thresholds as the health model's risk bands. */
export function scoreTone(score: number | null | undefined): Tone {
  if (score === null || score === undefined) return "neutral";
  return score < 40 ? "critical" : score < 60 ? "high" : score < 80 ? "medium" : "ok";
}

export function riskTone(risk: string | null | undefined): Tone {
  switch ((risk ?? "").toLowerCase()) {
    case "critical": return "critical";
    case "high": return "high";
    case "medium": return "medium";
    case "low": return "ok";
    default: return "neutral";
  }
}

/** One number that matters: big value, small uppercase label, optional hint and signed delta. */
export function Tile({ value, unit, label, hint, tone = "neutral", delta, deltaGoodWhenUp = true, onClick, title }: {
  value: ReactNode;
  unit?: string;
  label: ReactNode;
  hint?: ReactNode;
  tone?: Tone;
  delta?: number | null;
  deltaGoodWhenUp?: boolean;
  onClick?: () => void;
  title?: string;
}) {
  const body = (
    <>
      <span className="tile-v">
        {value}
        {unit && <small>{unit}</small>}
      </span>
      <span className="tile-l">{label}</span>
      {delta !== undefined && delta !== null && delta !== 0 && (
        <span className={`tile-d ${(delta > 0) === deltaGoodWhenUp ? "up" : "down"}`}>{delta > 0 ? `▲ +${delta}` : `▼ ${delta}`}</span>
      )}
      {hint && <span className="tile-h">{hint}</span>}
    </>
  );
  const cls = `tile tone-${tone}${onClick ? " link" : ""}`;
  return onClick ? (
    <div className={cls} role="button" tabIndex={0} title={title} onClick={onClick} onKeyDown={(e) => (e.key === "Enter" || e.key === " ") && onClick()}>
      {body}
    </div>
  ) : (
    <div className={cls} title={title}>{body}</div>
  );
}

/** Nothing here yet: a glyph, a sentence, and the one thing to do next. */
export function EmptyState({ glyph = "◈", title, text, action, children }: { glyph?: string; title: ReactNode; text?: ReactNode; action?: ReactNode; children?: ReactNode }) {
  return (
    <div className="empty-state">
      <div className="glyph" aria-hidden>{glyph}</div>
      <h2>{title}</h2>
      {text && <p>{text}</p>}
      {children}
      {action && <div className="actions" style={{ justifyContent: "center" }}>{action}</div>}
    </div>
  );
}

/** Loading placeholder shaped like the content it replaces. */
export function Skeleton({ kind = "line", count = 1, className }: { kind?: "line" | "tile" | "block"; count?: number; className?: string }) {
  const items = Array.from({ length: count }, (_, i) => <div key={i} className={`skeleton sk-${kind}`} aria-hidden />);
  if (kind === "tile") return <div className={`kpis${className ? ` ${className}` : ""}`}>{items}</div>;
  return <div className={className}>{items}</div>;
}

/** Signed number with a sign prefix, or an em dash. */
export function signed(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  return n > 0 ? `+${n}` : `${n}`;
}
