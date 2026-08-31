import { useMemo, type ReactNode } from "react";
import { Bar, BarChart, CartesianGrid, Cell, LabelList, Line, LineChart, Pie, PieChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { useThemeVersion } from "../theme";
import { riskTone, scoreTone, type Tone } from "./ui";

/** Chart colors come from the same CSS tokens as the rest of the UI, so light/dark stay in step. */
export interface Tokens {
  critical: string; high: string; medium: string; low: string; informational: string;
  ok: string; accent: string; legacy: string; modern: string; unknown: string;
  ink: string; soft: string; faint: string; line: string; surface: string;
}

const VARS: Record<keyof Tokens, string> = {
  critical: "--crit", high: "--high", medium: "--med", low: "--low", informational: "--info",
  ok: "--ok", accent: "--accent", legacy: "--legacy", modern: "--modern", unknown: "--unknown",
  ink: "--ink", soft: "--soft", faint: "--faint", line: "--line", surface: "--surface",
};

export function readTokens(): Tokens {
  const style = typeof window === "undefined" ? null : getComputedStyle(document.documentElement);
  const out = {} as Tokens;
  for (const [key, cssVar] of Object.entries(VARS) as [keyof Tokens, string][]) {
    out[key] = style?.getPropertyValue(cssVar).trim() || "#888";
  }
  return out;
}

export function useTokens(): Tokens {
  const version = useThemeVersion();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(readTokens, [version]);
}

export function toneColor(tk: Tokens, tone: Tone): string {
  switch (tone) {
    case "critical": return tk.critical;
    case "high": return tk.high;
    case "medium": return tk.medium;
    case "low":
    case "ok": return tk.ok;
    case "accent": return tk.accent;
    default: return tk.faint;
  }
}

export const SEVERITIES = ["Critical", "High", "Medium", "Low", "Informational"] as const;
export const RISKS = ["Critical", "High", "Medium", "Low"] as const;

export function severityColor(tk: Tokens, severity: string): string {
  switch (severity) {
    case "Critical": return tk.critical;
    case "High": return tk.high;
    case "Medium": return tk.medium;
    case "Low": return tk.low;
    default: return tk.informational;
  }
}

/* ---------- tooltip ---------- */

interface TipProps {
  active?: boolean;
  label?: string | number;
  payload?: ReadonlyArray<{ name?: string | number; value?: number | string; color?: string; fill?: string; payload?: Record<string, unknown> }>;
  format?: (value: number | string | undefined, name: string | number | undefined, row: Record<string, unknown> | undefined) => ReactNode;
  title?: (label: string | number | undefined, row: Record<string, unknown> | undefined) => ReactNode;
}

export function ChartTip({ active, label, payload, format, title }: TipProps) {
  if (!active || !payload || payload.length === 0) return null;
  const row = payload[0]?.payload;
  const head = title ? title(label, row) : (row?.name as string | undefined) ?? label;
  return (
    <div className="tip">
      {head !== undefined && head !== "" && <b>{head}</b>}
      {payload.map((p, i) => (
        <div key={i}>
          <span style={{ color: p.color ?? p.fill }}>●</span> {format ? format(p.value, p.name, row) : `${p.name ?? ""}: ${p.value ?? ""}`}
        </div>
      ))}
    </div>
  );
}

/* ---------- score ring (pure SVG) ---------- */

export function ScoreRing({ score, risk, size = 128, stroke = 10, caption }: { score: number | null | undefined; risk?: string | null; size?: number; stroke?: number; caption?: ReactNode }) {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const value = score ?? 0;
  const tone = risk ? riskTone(risk) : scoreTone(score);
  const cls = tone === "ok" ? "low" : tone === "neutral" ? "none" : tone;
  return (
    <div className={`ring risk-${cls}`} style={{ width: size, height: size }} role="img" aria-label={`${score ?? "—"} / 100`}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        <circle className="ring-track" cx={size / 2} cy={size / 2} r={r} fill="none" strokeWidth={stroke} />
        <circle
          className="ring-fill"
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={`${(c * value) / 100} ${c}`}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
          style={{ transition: "stroke-dasharray 0.6s ease" }}
        />
      </svg>
      <div className="ring-v">
        <b style={{ fontSize: size * 0.3 }}>{score ?? "—"}</b>
        {caption !== undefined ? <small>{caption}</small> : <small>/100</small>}
      </div>
    </div>
  );
}

/* ---------- donut: severity / risk / category distribution ---------- */

export function Donut({ data, colors, height = "h-md", centerLabel, emptyText, format }: {
  data: { name: string; key: string; value: number }[];
  colors: (key: string) => string;
  height?: "h-sm" | "h-md" | "h-lg";
  centerLabel?: ReactNode;
  emptyText: string;
  format?: (value: number | string | undefined, name: string | number | undefined) => ReactNode;
}) {
  const tk = useTokens();
  const total = data.reduce((s, d) => s + d.value, 0);
  const rows = data.filter((d) => d.value > 0);
  return (
    <div className={`chart ${height}`} style={{ position: "relative" }}>
      {total === 0 ? (
        <div className="chart-empty">{emptyText}</div>
      ) : (
        <>
          <ResponsiveContainer width="100%" height="100%">
            <PieChart>
              <Pie data={rows} dataKey="value" nameKey="name" innerRadius="62%" outerRadius="88%" paddingAngle={rows.length > 1 ? 2 : 0} stroke={tk.surface} strokeWidth={2} isAnimationActive={false}>
                {rows.map((d) => (
                  <Cell key={d.key} fill={colors(d.key)} />
                ))}
              </Pie>
              <Tooltip content={<ChartTip format={format ?? ((v, n) => `${n}: ${v}`)} title={() => ""} />} />
            </PieChart>
          </ResponsiveContainer>
          <div className="ring-v" style={{ position: "absolute", inset: 0, display: "grid", placeItems: "center", pointerEvents: "none" }}>
            <div style={{ textAlign: "center" }}>
              <b style={{ fontSize: "1.5rem" }}>{total}</b>
              {centerLabel && <small>{centerLabel}</small>}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

export function Legend({ items }: { items: { label: string; color: string; value?: ReactNode }[] }) {
  return (
    <div className="legend">
      {items.map((i) => (
        <span key={i.label}>
          <i style={{ background: i.color }} />
          {i.label}
          {i.value !== undefined && <b style={{ marginLeft: "0.3rem" }}>{i.value}</b>}
        </span>
      ))}
    </div>
  );
}

/* ---------- horizontal bars ---------- */

export interface HBarRow { name: string; value: number; color?: string; hint?: string }

export function HBars({ data, height = "h-md", max, valueFormat, emptyText, labelWidth = 120 }: {
  data: HBarRow[];
  height?: "h-sm" | "h-md" | "h-lg";
  max?: number;
  valueFormat?: (v: number) => string;
  emptyText?: string;
  labelWidth?: number;
}) {
  const tk = useTokens();
  if (data.length === 0 || data.every((d) => d.value === 0)) {
    return <div className={`chart ${height}`}><div className="chart-empty">{emptyText ?? "—"}</div></div>;
  }
  const fmt = valueFormat ?? ((v: number) => String(v));
  return (
    <div className={`chart ${height}`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 56, bottom: 4, left: 4 }} barCategoryGap="28%">
          <CartesianGrid horizontal={false} stroke={tk.line} strokeDasharray="3 3" />
          <XAxis type="number" domain={[0, max ?? "auto"]} hide />
          <YAxis type="category" dataKey="name" width={labelWidth} tick={{ fill: tk.ink, fontSize: 12 }} axisLine={false} tickLine={false} interval={0} />
          <Tooltip cursor={{ fill: tk.line, opacity: 0.35 }} content={<ChartTip format={(v, _n, row) => `${fmt(Number(v))}${row?.hint ? ` · ${row.hint}` : ""}`} />} />
          <Bar dataKey="value" radius={[0, 4, 4, 0]} isAnimationActive={false} minPointSize={2}>
            {data.map((d, i) => (
              <Cell key={i} fill={d.color ?? tk.accent} />
            ))}
            <LabelList dataKey="value" position="right" formatter={(v: unknown) => fmt(Number(v))} style={{ fill: tk.soft, fontSize: 12, fontVariantNumeric: "tabular-nums" }} />
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

/* ---------- stacked horizontal bars (e.g. frameworks legacy/modern, folder heat) ---------- */

export function StackedHBars({ data, keys, height = "h-md", labelWidth = 120, emptyText }: {
  data: Record<string, string | number>[];
  keys: { key: string; label: string; color: string }[];
  height?: "h-sm" | "h-md" | "h-lg";
  labelWidth?: number;
  emptyText?: string;
}) {
  const tk = useTokens();
  if (data.length === 0) return <div className={`chart ${height}`}><div className="chart-empty">{emptyText ?? "—"}</div></div>;
  return (
    <div className={`chart ${height}`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 16, bottom: 4, left: 4 }} barCategoryGap="28%">
          <CartesianGrid horizontal={false} stroke={tk.line} strokeDasharray="3 3" />
          <XAxis type="number" hide />
          <YAxis type="category" dataKey="name" width={labelWidth} tick={{ fill: tk.ink, fontSize: 12 }} axisLine={false} tickLine={false} interval={0} />
          <Tooltip cursor={{ fill: tk.line, opacity: 0.35 }} content={<ChartTip />} />
          {keys.map((k, i) => (
            <Bar key={k.key} dataKey={k.key} name={k.label} stackId="s" fill={k.color} radius={i === keys.length - 1 ? [0, 4, 4, 0] : 0} isAnimationActive={false} />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

/* ---------- vertical bars (fit score per strategy, counts per run) ---------- */

export function VBars({ data, height = "h-md", max, valueFormat, emptyText }: {
  data: HBarRow[];
  height?: "h-sm" | "h-md" | "h-lg";
  max?: number;
  valueFormat?: (v: number) => string;
  emptyText?: string;
}) {
  const tk = useTokens();
  if (data.length === 0) return <div className={`chart ${height}`}><div className="chart-empty">{emptyText ?? "—"}</div></div>;
  const fmt = valueFormat ?? ((v: number) => String(v));
  return (
    <div className={`chart ${height}`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} margin={{ top: 18, right: 8, bottom: 4, left: 0 }} barCategoryGap="30%">
          <CartesianGrid vertical={false} stroke={tk.line} strokeDasharray="3 3" />
          <XAxis dataKey="name" tick={{ fill: tk.soft, fontSize: 11 }} axisLine={false} tickLine={false} interval={0} />
          <YAxis domain={[0, max ?? "auto"]} hide />
          <Tooltip cursor={{ fill: tk.line, opacity: 0.35 }} content={<ChartTip format={(v, _n, row) => `${fmt(Number(v))}${row?.hint ? ` · ${row.hint}` : ""}`} />} />
          <Bar dataKey="value" radius={[4, 4, 0, 0]} isAnimationActive={false}>
            {data.map((d, i) => (
              <Cell key={i} fill={d.color ?? tk.accent} />
            ))}
            <LabelList dataKey="value" position="top" formatter={(v: unknown) => fmt(Number(v))} style={{ fill: tk.soft, fontSize: 11 }} />
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

/* ---------- trend line (health per run) ---------- */

export interface TrendPoint { x: string; value: number | null; hint?: string }

export function TrendLine({ points, height = "h-md", target, domain = [0, 100], emptyText, color }: {
  points: TrendPoint[];
  height?: "h-sm" | "h-md" | "h-lg";
  target?: number | null;
  domain?: [number, number];
  emptyText?: string;
  color?: string;
}) {
  const tk = useTokens();
  const valid = points.filter((p) => p.value !== null);
  if (valid.length < 2) return <div className={`chart ${height}`}><div className="chart-empty">{emptyText ?? "—"}</div></div>;
  const first = valid[0].value ?? 0;
  const last = valid[valid.length - 1].value ?? 0;
  const stroke = color ?? (last > first ? tk.ok : last < first ? tk.high : tk.accent);
  return (
    <div className={`chart ${height}`}>
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={points} margin={{ top: 10, right: 12, bottom: 0, left: -18 }}>
          <CartesianGrid vertical={false} stroke={tk.line} strokeDasharray="3 3" />
          <XAxis dataKey="x" tick={{ fill: tk.soft, fontSize: 11 }} axisLine={false} tickLine={false} />
          <YAxis domain={domain} tick={{ fill: tk.soft, fontSize: 11 }} axisLine={false} tickLine={false} width={40} />
          <Tooltip content={<ChartTip title={(l, row) => (row?.hint as string | undefined) ?? String(l ?? "")} format={(v) => `${v} / 100`} />} />
          {target !== null && target !== undefined && <ReferenceLine y={target} stroke={tk.accent} strokeDasharray="4 4" label={{ value: String(target), position: "right", fill: tk.accent, fontSize: 11 }} />}
          <Line type="monotone" dataKey="value" stroke={stroke} strokeWidth={2.5} dot={{ r: 3.5, fill: stroke, strokeWidth: 0 }} activeDot={{ r: 5 }} connectNulls isAnimationActive={false} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}

/* ---------- stacked vertical bars (findings new/resolved/regressed per run) ---------- */

export function StackedVBars({ data, keys, height = "h-md", emptyText }: {
  data: Record<string, string | number>[];
  keys: { key: string; label: string; color: string }[];
  height?: "h-sm" | "h-md" | "h-lg";
  emptyText?: string;
}) {
  const tk = useTokens();
  if (data.length === 0) return <div className={`chart ${height}`}><div className="chart-empty">{emptyText ?? "—"}</div></div>;
  return (
    <div className={`chart ${height}`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: -18 }} barCategoryGap="30%">
          <CartesianGrid vertical={false} stroke={tk.line} strokeDasharray="3 3" />
          <XAxis dataKey="name" tick={{ fill: tk.soft, fontSize: 11 }} axisLine={false} tickLine={false} />
          <YAxis tick={{ fill: tk.soft, fontSize: 11 }} axisLine={false} tickLine={false} width={40} allowDecimals={false} />
          <Tooltip cursor={{ fill: tk.line, opacity: 0.35 }} content={<ChartTip />} />
          {keys.map((k, i) => (
            <Bar key={k.key} dataKey={k.key} name={k.label} stackId="s" fill={k.color} radius={i === keys.length - 1 ? [4, 4, 0, 0] : 0} isAnimationActive={false} />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

/* ---------- mirrored bars: A to the left, B to the right, same scale ---------- */

export function MirrorBars({ rows, labelA, labelB, height = "h-md", labelWidth = 110, colorA, colorB, format }: {
  rows: { name: string; a: number; b: number }[];
  labelA: string;
  labelB: string;
  height?: "h-sm" | "h-md" | "h-lg";
  labelWidth?: number;
  colorA?: string;
  colorB?: string;
  format?: (v: number) => string;
}) {
  const tk = useTokens();
  const fmt = format ?? ((v: number) => String(v));
  const data = rows.map((r) => ({ name: r.name, a: -r.a, b: r.b }));
  const raw = Math.max(1, ...rows.flatMap((r) => [r.a, r.b]));
  const step = Math.pow(10, Math.floor(Math.log10(raw)));
  const max = Math.ceil(raw / step) * step; // round the scale up to a clean number so the ticks read well
  const ticks = [-max, -max / 2, 0, max / 2, max];
  return (
    <div className={`chart ${height}`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" stackOffset="sign" margin={{ top: 4, right: 12, bottom: 4, left: 4 }} barCategoryGap="30%">
          <CartesianGrid horizontal={false} stroke={tk.line} strokeDasharray="3 3" />
          <XAxis type="number" domain={[-max, max]} ticks={ticks} tickFormatter={(v) => fmt(Math.abs(Number(v)))} tick={{ fill: tk.soft, fontSize: 11 }} axisLine={false} tickLine={false} />
          <YAxis type="category" dataKey="name" width={labelWidth} tick={{ fill: tk.ink, fontSize: 12 }} axisLine={false} tickLine={false} interval={0} />
          <ReferenceLine x={0} stroke={tk.line} />
          <Tooltip cursor={{ fill: tk.line, opacity: 0.35 }} content={<ChartTip format={(v, n) => `${n}: ${fmt(Math.abs(Number(v)))}`} />} />
          <Bar dataKey="a" name={labelA} stackId="m" fill={colorA ?? tk.low} radius={[4, 0, 0, 4]} isAnimationActive={false} />
          <Bar dataKey="b" name={labelB} stackId="m" fill={colorB ?? tk.accent} radius={[0, 4, 4, 0]} isAnimationActive={false} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

/* ---------- roadmap gantt (CSS bars; phases run sequentially) ---------- */

export function RoadmapGantt({ phases, unit, hint }: {
  phases: { key: string; name: string; durationMonths: { optimistic: number; likely: number; conservative: number }; effortShare: number }[];
  unit: string;
  hint?: string;
}) {
  const total = Math.max(0.1, phases.reduce((s, p) => s + p.durationMonths.conservative, 0));
  let cursor = 0;
  const ticks = Array.from({ length: 5 }, (_, i) => Math.round((total * i) / 4 * 10) / 10);
  return (
    <div className="gantt" role="img" aria-label="roadmap">
      {phases.map((p) => {
        const start = cursor;
        cursor += p.durationMonths.likely;
        const left = (start / total) * 100;
        const likely = (p.durationMonths.likely / total) * 100;
        const tail = ((p.durationMonths.conservative - p.durationMonths.likely) / total) * 100;
        return (
          <div className="gantt-row" key={p.key}>
            <span className="gantt-name" title={p.name}>{p.name}</span>
            <div className="gantt-track" title={`${p.durationMonths.optimistic} – ${p.durationMonths.conservative} ${unit}`}>
              <span className="gantt-bar" style={{ left: `${left}%`, width: `${Math.max(0.8, likely)}%` }} />
              {tail > 0 && <span className="gantt-tail" style={{ left: `${left + likely}%`, width: `${tail}%` }} />}
            </div>
            <span className="num muted small">{p.durationMonths.likely} {unit} · {Math.round(p.effortShare * 100)}%</span>
          </div>
        );
      })}
      <div className="gantt-row">
        <span />
        <div className="gantt-axis">{ticks.map((tk, i) => <span key={i}>{tk}</span>)}</div>
        <span />
      </div>
      {hint && <p className="muted small" style={{ margin: 0 }}>{hint}</p>}
    </div>
  );
}

/* ---------- range bars: P25–P75 band, P50 mark, best/worst dots (benchmark) ---------- */

export function RangeBars({ rows, max = 100 }: { rows: { name: string; p25: number; p50: number; p75: number; best: number; worst: number; marker?: number | null }[]; max?: number }) {
  const pct = (v: number) => `${Math.min(100, Math.max(0, (v / max) * 100))}%`;
  return (
    <div className="range">
      {rows.map((r) => (
        <div className="range-row" key={r.name}>
          <span title={r.name}>{r.name}</span>
          <div className="range-track" title={`P25 ${r.p25} · P50 ${r.p50} · P75 ${r.p75}`}>
            <span className="range-band" style={{ left: pct(r.p25), width: pct(r.p75 - r.p25) }} />
            <span className="range-p50" style={{ left: pct(r.p50) }} />
            <span className="range-dot worst" style={{ left: pct(r.worst) }} title={`${r.worst}`} />
            <span className="range-dot best" style={{ left: pct(r.best) }} title={`${r.best}`} />
            {r.marker !== undefined && r.marker !== null && <span className="range-marker" style={{ left: pct(r.marker) }} title={`${r.marker}`} />}
          </div>
          <span className="num strong">{r.p50}</span>
        </div>
      ))}
    </div>
  );
}
