import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { AssessmentAiEstate, PortfolioAiEstate } from "../api";
import { I18nProvider } from "../i18n";
import { EstateOverview, rowFlags, SpendView } from "../pages/AiEstatePage";
import { AiEstateView } from "../pages/AiEstatePanel";
import { AllowlistEditor, groupByKind } from "../pages/AllowlistCard";
import { waiverState } from "../pages/WaiversPanel";
import { CliGuide, ForecastTiles, LiveView, UsageView } from "../pages/DeveloperUsageCard";
import { guideEndpoints, isGuideProvider, ProviderGuide } from "../pages/ProviderGuides";
import type { AiUsageSummary, LiveUsage, PriceCatalog, UsageForecast } from "../api";
import { draftFrom, ModelPricesView } from "../pages/ModelPricesCard";

function wrap(ui: React.ReactElement) {
  return render(<I18nProvider><MemoryRouter>{ui}</MemoryRouter></I18nProvider>);
}

const record: AssessmentAiEstate = {
  scanned: true,
  openPii: 4,
  openSecrets: 1,
  aiRules: [{ ruleId: "ai.unapproved-provider", title: "Unapproved provider: OpenAI", maxSeverity: "High", count: 1 }],
  record: {
    catalogVersion: "1.0.0",
    catalogHash: "abc",
    providers: [
      { id: "openai", name: "OpenAI", kind: "external-api", packages: ["npm:openai"], endpointFiles: 2, envVars: ["OPENAI_API_KEY"], models: ["gpt-4o"], codeFiles: 3, approved: false },
      { id: "ollama", name: "Ollama", kind: "local-runtime", packages: [], endpointFiles: 1, envVars: [], models: ["llama3.2"], codeFiles: 0, approved: true },
    ],
    frameworks: [{ name: "Semantic Kernel", packages: ["nuget:Microsoft.SemanticKernel"] }],
    mcp: [{ config: ".mcp.json", name: "docs", transport: "http", capability: "Read", remote: true, host: "mcp.context7.com", command: null, known: "Context7 docs", secrets: 0 }],
    activeModels: ["gpt-4o"],
    retiredModels: [{ model: "gpt-4-32k", retiredOn: "2025-06-06", replacement: "gpt-4.1", files: 2 }],
    vectorStores: [],
    localRuntimes: ["ollama"],
    gateways: [],
    allowlistConfigured: true,
    approved: ["azure-openai"],
    unapproved: ["openai"],
    secretsInMcp: 0,
  },
};

describe("AiEstateView", () => {
  it("shows providers with approval status, the correlation and the MCP table", () => {
    wrap(<AiEstateView data={record} />);
    expect(screen.getByText("OpenAI")).toBeInTheDocument();
    expect(screen.getByText("not approved")).toBeInTheDocument();
    expect(screen.getAllByText("approved").length).toBeGreaterThan(0);
    expect(screen.getByText(/1 external AI provider\(s\) in a repository with 4 open personal-data/)).toBeInTheDocument();
    expect(screen.getByText("mcp.context7.com")).toBeInTheDocument();
    expect(screen.getByText("gpt-4-32k")).toBeInTheDocument();
    expect(screen.getByText("Unapproved provider: OpenAI")).toBeInTheDocument();
    expect(screen.getByText(/Signature catalog 1\.0\.0/)).toBeInTheDocument();
  });

  it("explains the two empty cases apart", () => {
    wrap(<AiEstateView data={{ ...record, scanned: false, record: null }} />);
    expect(screen.getByText("Not scanned yet")).toBeInTheDocument();
    wrap(<AiEstateView data={{ ...record, record: null }} />);
    expect(screen.getByText("No AI detected")).toBeInTheDocument();
  });
});

describe("EstateOverview", () => {
  const data: PortfolioAiEstate = {
    assessmentsWithAi: 2,
    assessmentsScanned: 5,
    providers: [{ id: "openai", name: "OpenAI", kind: "external-api", assessments: 2, approved: false }],
    frameworks: [{ name: "LangChain", count: 1 }],
    mcpServers: 3,
    remoteMcpServers: 1,
    secretsInMcp: 0,
    retiredModelReferences: 2,
    allowlistConfigured: true,
    unapproved: [{ name: "openai", count: 2 }],
    aiWithPii: 1,
    aiWithSecrets: 0,
    rows: [{ assessmentId: "a1", name: "Billing", providers: ["OpenAI"], frameworks: ["LangChain"], mcpServers: 2, unapproved: 1, retiredModels: 1, secretsInMcp: 0, piiCoLocated: true, secretsCoLocated: false, tags: ["payments"] }],
    catalogVersion: "1.0.0",
    costs: null,
  };

  it("computes row flags in display order", () => {
    expect(rowFlags(data.rows[0]).map((f) => f.key)).toEqual(["aiestate.flag.unapproved", "aiestate.flag.pii", "aiestate.flag.retired"]);
    expect(rowFlags({ unapproved: 0, piiCoLocated: false, secretsCoLocated: false, retiredModels: 0, secretsInMcp: 0 })).toEqual([]);
  });

  it("renders tiles, the provider table and per-repository flags with links to the AI tab", () => {
    wrap(<EstateOverview data={data} />);
    expect(screen.getByText("Repositories using AI")).toBeInTheDocument();
    expect(screen.getByText("of 5 scanned")).toBeInTheDocument();
    expect(screen.getByText("unapproved provider")).toBeInTheDocument();
    expect(screen.getByText("PII co-located")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Billing" })).toHaveAttribute("href", "/assessments/a1?tab=ai");
    expect(screen.getByText("payments")).toBeInTheDocument();
  });
});

describe("SpendView", () => {
  it("keeps bases apart and shows seats and activity", () => {
    wrap(
      <SpendView
        costs={{
          days: 30,
          from: "2026-08-07",
          to: "2026-09-06",
          providers: [
            { provider: "openai", basis: "ProviderReported", currency: "USD", total: 16, topDimensions: [{ key: "p1 · GPT-4o", amount: 15 }], lastSyncAtUtc: "2026-09-06T10:00:00Z", lastSyncStatus: "Succeeded", lastSyncError: null },
            { provider: "github-copilot", basis: "Estimated", currency: "USD", total: 57, topDimensions: [], lastSyncAtUtc: null, lastSyncStatus: null, lastSyncError: null },
          ],
          seats: [{ provider: "github-copilot", total: 3, active: 1, idle: 2, pendingCancellation: 1, plan: "business" }],
          activity: [{ provider: "anthropic", key: "active-developers", averagePerDay: 3, unit: "developers" }],
          sourcesConfigured: 3,
        }}
      />,
    );
    expect(screen.getByText("OpenAI · provider-reported")).toBeInTheDocument();
    expect(screen.getByText("GitHub Copilot · estimated")).toBeInTheDocument();
    expect(screen.getByText("1 active · 2 idle 60+ days · 1 pending cancellation")).toBeInTheDocument();
    expect(screen.getByText("Anthropic (Claude, Claude Code) · active developers / day")).toBeInTheDocument();
  });
});

describe("AllowlistEditor", () => {
  const catalog = [
    { id: "openai", name: "OpenAI", kind: "external-api" },
    { id: "ollama", name: "Ollama", kind: "local-runtime" },
    { id: "anthropic", name: "Anthropic", kind: "external-api" },
  ];

  it("groups the catalog by kind with external providers first", () => {
    const groups = groupByKind(catalog);
    expect(groups.map(([k]) => k)).toEqual(["external-api", "local-runtime"]);
    expect(groups[0][1].map((p) => p.name)).toEqual(["Anthropic", "OpenAI"]);
  });

  it("renders the saved list, explains the source and enables save only when changed", () => {
    const onSave = vi.fn();
    wrap(<AllowlistEditor catalog={catalog} allowlist={{ approvedProviders: ["anthropic"], source: "tenant", updatedBy: "Ana", updatedAtUtc: "2026-09-06T10:00:00Z" }} onSave={onSave} onClear={() => {}} busy={false} message={null} error={null} />);
    expect(screen.getByText("saved in Atlas")).toBeInTheDocument();
    expect(screen.getByText(/Saved by Ana/)).toBeInTheDocument();
    const save = screen.getByRole("button", { name: "Save allowlist (1)" });
    expect(save).toBeDisabled();
    fireEvent.click(screen.getByLabelText(/OpenAI/));
    const save2 = screen.getByRole("button", { name: "Save allowlist (2)" });
    expect(save2).toBeEnabled();
    fireEvent.click(save2);
    expect(onSave).toHaveBeenCalledWith(expect.arrayContaining(["anthropic", "openai"]));
  });
});

describe("waiverState", () => {
  it("classifies revoked, expired and active waivers", () => {
    const now = Date.parse("2026-09-06T12:00:00Z");
    expect(waiverState({ revokedAtUtc: "2026-09-01T00:00:00Z", expiresAtUtc: null }, now)).toBe("revoked");
    expect(waiverState({ revokedAtUtc: null, expiresAtUtc: "2026-09-01T00:00:00Z" }, now)).toBe("expired");
    expect(waiverState({ revokedAtUtc: null, expiresAtUtc: "2026-12-01T00:00:00Z" }, now)).toBe("active");
    expect(waiverState({ revokedAtUtc: null, expiresAtUtc: null }, now)).toBe("active");
  });
});

describe("ProviderGuide", () => {
  it("knows the three cost providers and their read-only endpoints", () => {
    expect(isGuideProvider("openai") && isGuideProvider("anthropic") && isGuideProvider("github-copilot")).toBe(true);
    expect(isGuideProvider("bedrock")).toBe(false);
    expect(guideEndpoints("github-copilot")).toContain("GET /orgs/{org}/copilot/billing/seats");
  });

  it("renders numbered steps, the console link and what Atlas reads", () => {
    wrap(<ProviderGuide provider="anthropic" />);
    expect(screen.getByText("How to connect Anthropic (Claude, Claude Code)")).toBeInTheDocument();
    expect(screen.getByText(/sk-ant-admin-/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Open the provider console/ })).toHaveAttribute("href", expect.stringContaining("console.anthropic.com"));
    expect(screen.getByText("GET /v1/organizations/cost_report")).toBeInTheDocument();
    expect(screen.getByTestId("guide-anthropic").querySelectorAll("ol li")).toHaveLength(4);
  });

  it("renders nothing for a provider without a guide", () => {
    const { container } = wrap(<ProviderGuide provider="bedrock" />);
    expect(container.querySelector(".guide")).toBeNull();
  });
});

const forecast: UsageForecast = { monthToDateCost: 21, daysElapsedInMonth: 6, daysInMonth: 30, projectedMonthCost: 105, dailyAverage7: 3, dailyAverage30: 0.7, projectedNext30Cost: 90, trendPercent: 328.57, monthToDateTokens: 3_500_000, unpricedTokensMonthToDate: 200 };

const usage: AiUsageSummary = {
  days: 30, from: "2026-08-07", to: "2026-09-06", priceCatalogVersion: "list-2026-09", currency: "USD",
  reportingActors: 2, totalTokens: 3_500_000, estimatedCost: 21, unpricedTokens: 200,
  actors: [
    { actor: "bruno", tools: ["claude-code"], sessions: 3, requests: 40, tokens: 2_000_000, estimatedCost: 15, unpricedTokens: 0, lastReportUtc: "2026-09-06T10:00:00Z", lastPeriod: "2026-09-06", forecast: { ...forecast, monthToDateCost: 15, projectedMonthCost: 75 } },
    { actor: "anon-1a2b3c4d5e", tools: ["claude-code"], sessions: 2, requests: 10, tokens: 1_500_000, estimatedCost: 6, unpricedTokens: 200, lastReportUtc: "2026-09-05T10:00:00Z", lastPeriod: "2026-09-05", forecast: { ...forecast, monthToDateCost: 6, projectedMonthCost: 30 } },
  ],
  forecast,
  byModel: [
    { model: "claude-opus-4-1", tokens: 2_000_000, estimatedCost: 15, actors: 1 },
    { model: "claude-sonnet-4", tokens: 1_499_800, estimatedCost: 6, actors: 1 },
    { model: "in-house-llm", tokens: 200, estimatedCost: null, actors: 1 },
  ],
  byDay: [{ period: "2026-09-05", tokens: 1_500_000, estimatedCost: 6, actors: 1 }, { period: "2026-09-06", tokens: 2_000_000, estimatedCost: 15, actors: 1 }],
};

describe("UsageView", () => {
  it("shows per-developer estimated cost, the run rate and unpriced models as unpriced", () => {
    wrap(<UsageView usage={usage} />);
    expect(screen.getAllByText("Developers reporting").length).toBeGreaterThan(0);
    expect(screen.getAllByText("$21.00").length).toBeGreaterThan(0);
    expect(screen.getByText("$10.50")).toBeInTheDocument(); // per developer
    expect(screen.getByText("bruno")).toBeInTheDocument();
    expect(screen.getByText(/anon-1a2b3c4d5e/)).toBeInTheDocument();
    expect(screen.getByText("in-house-llm")).toBeInTheDocument();
    expect(screen.getAllByText("unpriced").length).toBeGreaterThan(0);
    expect(screen.getByText("Unpriced tokens")).toBeInTheDocument();
    expect(screen.getByText(/never summed with provider-reported spend/)).toBeInTheDocument();
  });
});

describe("CliGuide", () => {
  it("prints a dry-run command before the real one and links to API tokens and the release", () => {
    const { container } = wrap(<CliGuide server="https://atlas.example.com" />);
    const cmds = Array.from(container.querySelectorAll("pre.cmd code")).map((c) => c.textContent);
    expect(cmds[0]).toContain("--dry-run");
    expect(cmds[1]).toBe("atlas-agent --server https://atlas.example.com --token <ATLAS_TOKEN> --days 7");
    expect(screen.getByRole("link", { name: /API tokens/ })).toHaveAttribute("href", "/settings/tokens");
    expect(screen.getByRole("link", { name: /Download from GitHub releases/ })).toHaveAttribute("href", expect.stringContaining("/releases/latest"));
    expect(screen.getByText(/Never prompts, code, file paths or project names/)).toBeInTheDocument();
  });
});

describe("ModelPricesView", () => {
  const catalog: PriceCatalog = {
    version: "list-2026-09+tenant", baseVersion: "list-2026-09", currency: "USD", note: null,
    prices: [
      { id: "11111111-1111-1111-1111-111111111111", pattern: "^claude-opus-5", input: 5, output: 25, cacheRead: 0.5, cacheWrite: 6.25, source: "tenant", note: "contract", updatedBy: "Ana", updatedAtUtc: "2026-09-07T00:00:00Z" },
      { id: null, pattern: "^gpt-5-mini", input: 0.25, output: 2, cacheRead: 0.025, cacheWrite: null, source: "builtin", note: null, updatedBy: null, updatedAtUtc: null },
    ],
    unpriced: [{ model: "claude-fable-5-1", tokens: 102_623_400, actors: 2, lastSeen: "2026-09-07" }],
  };

  it("lists tenant lines first with edit/remove, builtin lines with override, and offers unpriced models as suggestions", () => {
    const picked: string[] = [];
    const removed: string[] = [];
    wrap(<ModelPricesView catalog={catalog} onPick={(d) => picked.push(d.pattern)} onRemove={(id) => removed.push(id)} />);
    expect(screen.getByText("^claude-opus-5")).toBeInTheDocument();
    expect(screen.getByText("yours")).toBeInTheDocument();
    expect(screen.getByText("builtin")).toBeInTheDocument();
    expect(screen.getByText(/1 model\(s\) in the reports have no price/)).toBeInTheDocument();
    fireEvent.click(screen.getByText("claude-fable-5-1"));
    expect(picked).toEqual(["^claude-fable-5-1$"]);
    fireEvent.click(screen.getByText("Override"));
    expect(picked[1]).toBe("^gpt-5-mini");
    fireEvent.click(screen.getByText("Remove"));
    expect(removed).toEqual(["11111111-1111-1111-1111-111111111111"]);
  });

  it("turns a price line into an editable draft", () => {
    const d = draftFrom(catalog.prices[1]);
    expect(d).toEqual({ pattern: "^gpt-5-mini", input: "0.25", output: "2", cacheRead: "0.025", cacheWrite: "", note: "" });
  });
});

describe("ForecastTiles and LiveView", () => {
  it("shows month-to-date, projected month, next-30 and trend with their formulas", () => {
    wrap(<ForecastTiles forecast={forecast} currency="USD" lang="en" />);
    expect(screen.getAllByText("$21.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("$105.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("$90.00").length).toBeGreaterThan(0);
    expect(screen.getAllByText("+329%").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/month to date ÷ days elapsed × days in month/).length).toBeGreaterThan(0);
    expect(screen.getAllByText("Unpriced this month").length).toBeGreaterThan(0);
  });

  it("marks active developers, shows running totals and draws the intraday curve", () => {
    const live: LiveUsage = {
      period: "2026-09-07", asOfUtc: "2026-09-07T10:00:00Z", activeMinutes: 15, activeActors: 1, reportingActorsToday: 2,
      tokensToday: 8200, estimatedCostToday: 8.2, tokensLastHour: 4200,
      actors: [
        { actor: "ana", tools: ["claude-code", "codex"], lastReportUtc: "2026-09-07T09:58:00Z", activeNow: true, tokensToday: 5200, estimatedCostToday: 5.2, requestsToday: 12, tokensLastHour: 4200 },
        { actor: "bruno", tools: ["claude-code"], lastReportUtc: "2026-09-07T07:00:00Z", activeNow: false, tokensToday: 3000, estimatedCostToday: 3, requestsToday: 4, tokensLastHour: 0 },
      ],
      curve: [{ atUtc: "2026-09-07T07:15:00Z", tokens: 3000, estimatedCost: 3 }, { atUtc: "2026-09-07T10:00:00Z", tokens: 8200, estimatedCost: 8.2 }],
    };
    const { container } = wrap(<LiveView live={live} now={new Date("2026-09-07T10:00:00Z")} />);
    expect(screen.getByText("active")).toBeInTheDocument();
    expect(screen.getByText("idle")).toBeInTheDocument();
    expect(screen.getByText("2 min ago")).toBeInTheDocument();
    expect(screen.getByText("3 h ago")).toBeInTheDocument();
    expect(screen.getByText("+4.2k")).toBeInTheDocument();
    expect(container.querySelector("polyline")).not.toBeNull();
  });
});
