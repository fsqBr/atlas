import { useState } from "react";
import { Link } from "react-router-dom";
import type { AiUsageSummary } from "../api";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";

export type TelemetrySource = "claude-code" | "gemini-cli" | "collector" | "litellm" | "app";

/** Copy-paste setup per sender; `server` is the Atlas origin, the token comes from Settings → API tokens (analyst). */
export function telemetrySnippet(source: TelemetrySource, server: string): string {
  const endpoint = `${server}/api/ai-estate/otlp`;
  switch (source) {
    case "claude-code":
      return [
        "# Claude Code — set in the shell profile (or a managed settings file); nothing else to install",
        "export CLAUDE_CODE_ENABLE_TELEMETRY=1",
        "export OTEL_METRICS_EXPORTER=otlp",
        "export OTEL_EXPORTER_OTLP_PROTOCOL=http/json",
        `export OTEL_EXPORTER_OTLP_ENDPOINT=${endpoint}`,
        'export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <ATLAS_TOKEN>"',
        "export OTEL_METRIC_EXPORT_INTERVAL=60000   # ms; 60 s keeps the Live panel current",
        "# Windows PowerShell: use  $env:NAME = \"value\"  for each line",
      ].join("\n");
    case "gemini-cli":
      return [
        "# Gemini CLI — ~/.gemini/settings.json",
        "{",
        '  "telemetry": {',
        '    "enabled": true,',
        '    "target": "local",',
        `    "otlpEndpoint": "${endpoint}",`,
        '    "otlpProtocol": "http",',
        '    "logPrompts": false',
        "  }",
        "}",
        "# and in the shell:",
        "export OTEL_EXPORTER_OTLP_PROTOCOL=http/json",
        'export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <ATLAS_TOKEN>"',
      ].join("\n");
    case "collector":
      return [
        "# OpenTelemetry Collector — forward GenAI metrics/spans from anywhere (apps, gateways) to Atlas",
        "exporters:",
        "  otlphttp/atlas:",
        `    endpoint: ${endpoint}`,
        "    encoding: json",
        "    headers:",
        '      Authorization: "Bearer <ATLAS_TOKEN>"',
        "service:",
        "  pipelines:",
        "    metrics: { receivers: [otlp], exporters: [otlphttp/atlas] }",
        "    traces:  { receivers: [otlp], exporters: [otlphttp/atlas] }",
      ].join("\n");
    case "litellm":
      return [
        "# LiteLLM proxy — config.yaml; every request through the gateway becomes a gen_ai span",
        "litellm_settings:",
        "  callbacks: [\"otel\"]",
        "# environment of the proxy:",
        "OTEL_EXPORTER=otlp_http",
        `OTEL_ENDPOINT=${endpoint}/v1/traces`,
        'OTEL_HEADERS="Authorization=Bearer <ATLAS_TOKEN>"',
        "OTEL_EXPORTER_OTLP_PROTOCOL=http/json",
      ].join("\n");
    case "app":
      return [
        "# Your own application — any OpenTelemetry SDK with GenAI semantic conventions",
        `OTEL_EXPORTER_OTLP_ENDPOINT=${endpoint}`,
        "OTEL_EXPORTER_OTLP_PROTOCOL=http/json",
        'OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <ATLAS_TOKEN>"',
        "OTEL_SERVICE_NAME=payments-api        # shows up as svc:payments-api",
        "# spans need: gen_ai.request.model, gen_ai.provider.name (or gen_ai.system),",
        "#             gen_ai.usage.input_tokens, gen_ai.usage.output_tokens",
      ].join("\n");
  }
}

export function TelemetryCard({ usage }: { usage: AiUsageSummary | null }) {
  const { t } = useI18n();
  const [source, setSource] = useState<TelemetrySource>("claude-code");
  const server = typeof window !== "undefined" ? window.location.origin : "https://atlas.example.com";
  const otelTools = usage?.byTool.filter((x) => x.source.includes("otel")) ?? [];
  return (
    <Card title={t("telemetry.title")} subtitle={t("telemetry.hint")}>
      <div className="callout">
        <strong>{t("telemetry.whyTitle")}</strong> {t("telemetry.why")}
      </div>
      <div className="tabs" role="tablist" style={{ marginBottom: "0.6rem" }}>
        {(["claude-code", "gemini-cli", "collector", "litellm", "app"] as TelemetrySource[]).map((s) => (
          <button key={s} type="button" role="tab" aria-selected={source === s} className={`tab${source === s ? " active" : ""}`} onClick={() => setSource(s)}>
            {t(`telemetry.src.${s}` as "telemetry.src.claude-code")}
          </button>
        ))}
      </div>
      <ol className="guide-steps">
        <li>{t("telemetry.s1")} <Link to="/settings/tokens">{t("nav.tokens")} →</Link></li>
        <li>
          {t("telemetry.s2")}
          <pre className="cmd"><code>{telemetrySnippet(source, server)}</code></pre>
        </li>
        <li>{t("telemetry.s3")}</li>
      </ol>
      <p className="small muted">{t("telemetry.privacy")}</p>
      <h4 className="sub">{t("telemetry.seen")}</h4>
      {otelTools.length === 0 ? <p className="muted small">{t("telemetry.none")}</p> : (
        <div className="chips">{otelTools.map((x) => <span key={x.tool} className="chip"><code>{x.tool}</code> <small>{x.actors} {t("usage.devsShort")}</small></span>)}</div>
      )}
    </Card>
  );
}
