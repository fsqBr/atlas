import { useEffect, useState } from "react";
import { api, type AiEstateRecord, type AssessmentAiEstate } from "../api";
import { ErrorBox, SeverityChip } from "../components";
import { Card, EmptyState, Skeleton, Tile } from "../components/ui";
import { useI18n } from "../i18n";

type Tab = "overview" | "findings" | "ai" | "waivers" | "runs" | "modernization" | "rules" | "report" | "settings";

/** Provider kind → translated label; unknown kinds fall back to the raw value. */
export function KindTag({ kind }: { kind: string }) {
  const { t } = useI18n();
  const key = `aiestate.kind.${kind}` as "aiestate.kind.external-api";
  const label = t(key);
  return <span className={`kind ai-kind-${kind}`}>{label === key ? kind : label}</span>;
}

export function ApprovalPill({ approved, allowlist }: { approved: boolean | null; allowlist: boolean }) {
  const { t } = useI18n();
  if (!allowlist) return <span className="pill soft">{t("aiestate.noAllowlist")}</span>;
  return approved === false ? <span className="pill bad">{t("aiestate.notApproved")}</span> : <span className="pill ok">{t("aiestate.approved")}</span>;
}

/** Capability class of an MCP server as a pill: Execute and Write stand out, Read and None stay quiet. */
export function CapabilityPill({ capability }: { capability: string }) {
  const tone = capability === "Execute" ? "bad" : capability === "ReadWrite" || capability === "Write" ? "warn" : "soft";
  return <span className={`pill ${tone}`}>{capability}</span>;
}

/** The AI estate of one assessment, from persisted facts (the same the report section shows). Pure: takes the data. */
export function AiEstateView({ data, onGoTo }: { data: AssessmentAiEstate; onGoTo?: (tab: Tab) => void }) {
  const { t, formatNumber } = useI18n();

  if (!data.scanned) {
    return <EmptyState glyph="✦" title={t("aiestate.notScannedTitle")} text={t("aiestate.notScanned")} />;
  }

  const r: AiEstateRecord | null = data.record;
  if (!r) {
    return <EmptyState glyph="✦" title={t("aiestate.noneTitle")} text={t("aiestate.none")} />;
  }

  const external = r.providers.filter((p) => p.kind === "external-api");
  const infra = [...new Set([...r.localRuntimes, ...r.vectorStores, ...r.gateways])];
  const sdkPackages = r.providers.reduce((s, p) => s + p.packages.length, 0);
  const remote = r.mcp.filter((m) => m.remote).length;

  return (
    <>
      <div className="kpis">
        <Tile value={r.providers.length} label={t("aiestate.providers")} tone="accent" hint={`${external.length} ${t("aiestate.kind.external-api")}`} />
        <Tile value={sdkPackages} label={t("aiestate.sdks")} tone="neutral" />
        <Tile value={r.frameworks.length} label={t("aiestate.frameworks")} tone="neutral" />
        <Tile value={r.mcp.length} label={t("aiestate.mcpServers")} tone={remote > 0 ? "medium" : "neutral"} hint={remote > 0 ? t("aiestate.remote", { n: remote }) : undefined} />
        <Tile value={r.activeModels.length + r.retiredModels.length} label={t("aiestate.models")} tone="neutral" />
        <Tile value={r.retiredModels.length} label={t("aiestate.retired")} tone={r.retiredModels.length > 0 ? "medium" : "ok"} />
        {r.allowlistConfigured && <Tile value={r.unapproved.length} label={t("aiestate.unapproved")} tone={r.unapproved.length > 0 ? "critical" : "ok"} />}
        {r.secretsInMcp > 0 && <Tile value={r.secretsInMcp} label={t("aiestate.secretsMcp")} tone="critical" />}
      </div>

      {external.length > 0 && (
        <div className={`callout ${data.openPii > 0 || data.openSecrets > 0 ? "warn" : "ok"}`}>
          {data.openPii > 0 || data.openSecrets > 0
            ? t("aiestate.correlation", { external: external.length, pii: formatNumber(data.openPii), secrets: formatNumber(data.openSecrets) })
            : t("aiestate.correlationClean")}
          {onGoTo && (data.openPii > 0 || data.openSecrets > 0) && (
            <>
              {" "}
              <button type="button" className="link" onClick={() => onGoTo("findings")}>{t("aiestate.goFindings")}</button>
            </>
          )}
        </div>
      )}

      <Card title={t("aiestate.providersTitle")} subtitle={r.allowlistConfigured ? t("aiestate.allowlistOn") : t("aiestate.allowlistOff")}>
        <table>
          <thead>
            <tr>
              <th>{t("aiestate.provider")}</th>
              <th>{t("aiestate.kind")}</th>
              <th>{t("aiestate.evidence")}</th>
              <th>{t("aiestate.status")}</th>
            </tr>
          </thead>
          <tbody>
            {[...r.providers].sort((a, b) => (a.kind === "external-api" ? 0 : 1) - (b.kind === "external-api" ? 0 : 1) || a.name.localeCompare(b.name)).map((p) => (
              <tr key={p.id}>
                <td>
                  <div className="strong">{p.name}</div>
                  {p.packages.length > 0 && <div className="mono small muted">{p.packages.slice(0, 4).join(", ")}{p.packages.length > 4 ? " …" : ""}</div>}
                </td>
                <td><KindTag kind={p.kind} /></td>
                <td className="small">{t("aiestate.evidenceLine", { packages: p.packages.length, endpoints: p.endpointFiles, env: p.envVars.length, models: p.models.length, code: p.codeFiles })}</td>
                <td><ApprovalPill approved={p.approved} allowlist={r.allowlistConfigured} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {(r.frameworks.length > 0 || infra.length > 0) && (
          <div className="pill-rows">
            {r.frameworks.length > 0 && (
              <div className="pill-row">
                <span className="eyebrow">{t("aiestate.frameworks")}</span>
                {r.frameworks.map((f) => <span key={f.name} className="pill accent" title={f.packages.join(", ")}>{f.name}</span>)}
              </div>
            )}
            {infra.length > 0 && (
              <div className="pill-row">
                <span className="eyebrow">{t("aiestate.infra")}</span>
                {infra.map((i) => <span key={i} className="pill soft">{i}</span>)}
              </div>
            )}
          </div>
        )}
      </Card>

      {r.mcp.length > 0 && (
        <Card title={t("aiestate.mcpTitle")}>
          <table>
            <thead>
              <tr>
                <th>{t("aiestate.server")}</th>
                <th>{t("aiestate.config")}</th>
                <th>{t("aiestate.transport")}</th>
                <th>{t("aiestate.capability")}</th>
                <th>{t("aiestate.host")}</th>
              </tr>
            </thead>
            <tbody>
              {r.mcp.map((m) => (
                <tr key={`${m.config}:${m.name}`}>
                  <td>
                    <div className="strong">{m.name}</div>
                    {m.known && <div className="small muted">{m.known}</div>}
                    {m.secrets > 0 && <span className="flag flag-crit">{t("aiestate.flag.secretMcp")}</span>}
                  </td>
                  <td className="mono small">{m.config}</td>
                  <td>{m.transport}</td>
                  <td><CapabilityPill capability={m.capability} /></td>
                  <td className="mono small">
                    {m.host ?? m.command ?? "—"} {m.remote ? <span className="pill bad">{t("aiestate.remoteChip")}</span> : <span className="pill soft">{t("aiestate.localChip")}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {r.retiredModels.length > 0 && (
        <Card title={t("aiestate.retired")}>
          <table>
            <thead>
              <tr>
                <th>{t("aiestate.model")}</th>
                <th>{t("aiestate.retiredOn")}</th>
                <th>{t("aiestate.replacement")}</th>
                <th className="num">{t("aiestate.files")}</th>
              </tr>
            </thead>
            <tbody>
              {r.retiredModels.map((m) => (
                <tr key={m.model}>
                  <td className="mono">{m.model}</td>
                  <td>{m.retiredOn ?? "—"}</td>
                  <td>{m.replacement ?? "—"}</td>
                  <td className="num">{m.files}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {data.aiRules.length > 0 && (
        <Card title={t("aiestate.rulesTitle")} actions={onGoTo && <button type="button" className="button small" onClick={() => onGoTo("findings")}>{t("aiestate.goFindings")} →</button>}>
          <table>
            <thead>
              <tr>
                <th>{t("findings.severity")}</th>
                <th>{t("findings.finding")}</th>
                <th className="num">{t("findings.count")}</th>
              </tr>
            </thead>
            <tbody>
              {data.aiRules.map((g) => (
                <tr key={g.ruleId}>
                  <td><SeverityChip severity={g.maxSeverity} /></td>
                  <td>
                    <div className="strong">{g.title}</div>
                    <div className="mono small muted">{g.ruleId}</div>
                  </td>
                  <td className="num">{formatNumber(g.count)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      <p className="muted small">{t("aiestate.catalog", { version: r.catalogVersion })} · {t("aiestate.privacyNote")}</p>
    </>
  );
}

/** Fetches the assessment's AI estate and renders it; re-fetches when the run state changes. */
export function AiEstatePanel({ assessmentId, refreshKey, onGoTo }: { assessmentId: string; refreshKey: string; onGoTo: (tab: Tab) => void }) {
  const { t, lang } = useI18n();
  const [data, setData] = useState<AssessmentAiEstate | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setData(null);
    api.getAssessmentAiEstate(assessmentId, lang).then((d) => alive && setData(d)).catch(() => alive && setError(t("error.load")));
    return () => {
      alive = false;
    };
  }, [assessmentId, lang, refreshKey, t]);

  if (error) return <ErrorBox message={error} />;
  if (!data) return <><Skeleton kind="tile" count={6} /><Skeleton kind="block" /></>;
  return <AiEstateView data={data} onGoTo={onGoTo} />;
}
