import { useI18n } from "../i18n";

/**
 * "How to connect" per cost provider: where the admin key comes from, which permission it needs, which
 * read-only endpoint Atlas calls and what Atlas reads. Text lives in i18n so the guide is bilingual.
 */
export type GuideProvider = "openai" | "anthropic" | "github-copilot" | "cursor";

const GUIDES: Record<GuideProvider, { steps: number; docs: string; endpoints: string[] }> = {
  openai: {
    steps: 4,
    docs: "https://platform.openai.com/settings/organization/admin-keys",
    endpoints: ["GET /v1/organization/costs"],
  },
  anthropic: {
    steps: 4,
    docs: "https://console.anthropic.com/settings/admin-keys",
    endpoints: ["GET /v1/organizations/cost_report", "GET /v1/organizations/usage_report/claude_code"],
  },
  "github-copilot": {
    steps: 4,
    docs: "https://github.com/settings/tokens?type=beta",
    endpoints: ["GET /orgs/{org}/copilot/billing", "GET /orgs/{org}/copilot/billing/seats"],
  },
  cursor: {
    steps: 4,
    docs: "https://cursor.com/settings",
    endpoints: ["GET /teams/members", "POST /teams/spend", "POST /teams/filtered-usage-events"],
  },
};

export function isGuideProvider(p: string): p is GuideProvider {
  return p in GUIDES;
}

export function guideEndpoints(provider: GuideProvider): string[] {
  return GUIDES[provider].endpoints;
}

export function ProviderGuide({ provider }: { provider: string }) {
  const { t } = useI18n();
  if (!isGuideProvider(provider)) return null;
  const g = GUIDES[provider];
  const key = provider === "github-copilot" ? "copilot" : provider;
  const steps = Array.from({ length: g.steps }, (_, i) => t(`guide.${key}.s${i + 1}` as "guide.openai.s1"));
  return (
    <div className="guide" data-testid={`guide-${provider}`}>
      <div className="guide-head">
        <strong>{t("guide.title", { provider: t(`aiestate.provider.${provider}` as "aiestate.provider.openai") })}</strong>
        <a href={g.docs} target="_blank" rel="noreferrer" className="small">{t("guide.docs")} ↗</a>
      </div>
      <ol className="guide-steps">
        {steps.map((s, i) => <li key={i}>{s}</li>)}
      </ol>
      <div className="guide-foot">
        <span className="small muted">{t("guide.reads")} {t(`guide.${key}.reads` as "guide.openai.reads")}</span>
        <span className="guide-endpoints">{g.endpoints.map((e) => <code key={e}>{e}</code>)}</span>
      </div>
      <p className="small muted guide-note">{t("guide.readOnly")}</p>
    </div>
  );
}
