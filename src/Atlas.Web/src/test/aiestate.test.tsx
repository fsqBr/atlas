import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import type { AssessmentAiEstate, PortfolioAiEstate } from "../api";
import { I18nProvider } from "../i18n";
import { EstateOverview, rowFlags, SpendView } from "../pages/AiEstatePage";
import { AiEstateView } from "../pages/AiEstatePanel";

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
