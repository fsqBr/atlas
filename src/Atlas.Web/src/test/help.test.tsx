import { render, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { Markdown } from "../components/Markdown";
import { PageHeader } from "../components/ui";
import { HELP_GROUPS, HELP_PLACEHOLDERS, HELP_SECTIONS, helpSectionForRoute } from "../help/content";
import { I18nProvider } from "../i18n";
import { HelpPage, resolvePlaceholders } from "../pages/HelpPage";

vi.mock("../api", () => ({
  api: {
    getVersion: () => Promise.resolve({ version: "9.9.9" }),
    authConfig: () => Promise.resolve({ enabled: false }),
    me: () => Promise.resolve({ name: null, tenantId: null, tenantName: null, isDefaultTenant: true, roles: ["admin"] }),
    costSchedule: () => Promise.resolve({ enabled: true, hourUtc: 6, days: 7, nextRunUtc: "2026-09-08T06:00:00Z", lastRunUtc: null, lastTenants: 0, lastSources: 0, lastFailures: 0 }),
    getUsagePrices: () => Promise.resolve({ version: "list-2026-09", baseVersion: "list-2026-09", currency: "USD", note: null, prices: [], unpriced: [{ model: "x", tokens: 1, actors: 1, lastSeen: "" }] }),
    costProviders: () => Promise.resolve(["openai", "anthropic", "github-copilot", "cursor"]),
    catalogProviders: () => Promise.resolve([{ id: "openai", name: "OpenAI", kind: "external-api" }]),
  },
}));

const placeholders = (s: string) => (s.match(/\{[a-zA-Z]+\}/g) ?? []).filter((p) => (HELP_PLACEHOLDERS as readonly string[]).includes(p.slice(1, -1))).sort();
const fences = (s: string) => (s.match(/```/g) ?? []).length;

describe("help content", () => {
  it("has unique ids, known groups and both languages for every section", () => {
    const ids = HELP_SECTIONS.map((s) => s.id);
    expect(new Set(ids).size).toBe(ids.length);
    for (const s of HELP_SECTIONS) {
      expect(HELP_GROUPS).toContain(s.group);
      expect(s.title.en.trim().length).toBeGreaterThan(0);
      expect(s.title["pt-BR"].trim().length).toBeGreaterThan(0);
      expect(s.body.en.trim().length, s.id).toBeGreaterThan(80);
      expect(s.body["pt-BR"].trim().length, s.id).toBeGreaterThan(80);
    }
  });

  it("keeps placeholders and code fences in step between languages", () => {
    for (const s of HELP_SECTIONS) {
      expect(placeholders(s.body["pt-BR"]), s.id).toEqual(placeholders(s.body.en));
      expect(fences(s.body["pt-BR"]), s.id).toBe(fences(s.body.en));
      expect(fences(s.body.en) % 2, `${s.id}: unbalanced fence`).toBe(0);
    }
  });

  it("only links to sections that exist", () => {
    const ids = new Set(HELP_SECTIONS.map((s) => s.id));
    for (const s of HELP_SECTIONS) {
      for (const lang of ["en", "pt-BR"] as const) {
        for (const m of s.body[lang].matchAll(/\]\(\/help#([a-z-]+)\)/g)) {
          expect(ids.has(m[1]), `${s.id} (${lang}) links to missing #${m[1]}`).toBe(true);
        }
      }
    }
  });

  it("covers every UI route with a contextual section", () => {
    const routes = ["/", "/assessments", "/new", "/portfolio", "/compare", "/jobs", "/rules", "/credentials", "/settings/ai", "/settings/cost", "/settings/admin", "/settings/tokens", "/ai-estate"];
    for (const r of routes) expect(helpSectionForRoute(r), r).not.toBeNull();
    expect(helpSectionForRoute("/ai-estate", "?tab=budgets")).toBe("budgets");
    expect(helpSectionForRoute("/ai-estate", "?tab=spend")).toBe("spend");
    expect(helpSectionForRoute("/assessments/123e4567")).toBe("assessment-overview");
    expect(helpSectionForRoute("/assessments/123e4567", "?tab=waivers")).toBe("waivers");
    expect(helpSectionForRoute("/nowhere")).toBeNull();
  });

  it("resolves known placeholders and leaves route templates alone", () => {
    const out = resolvePlaceholders("v{version} at {origin}; GET /api/assessments/{id}/gate", { version: "1.2.3", origin: "http://x" });
    expect(out).toBe("v1.2.3 at http://x; GET /api/assessments/{id}/gate");
    expect(resolvePlaceholders("{syncHour}", {})).toBe("…");
  });
});

describe("Markdown tables and links", () => {
  it("renders pipe tables with code-safe cells and links only when enabled", () => {
    const md = "| A | B |\n|---|---|\n| `csv|json` | [Rules](/rules) *it* |";
    const { container } = render(<Markdown text={md} links />);
    const cells = container.querySelectorAll("tbody td");
    expect(cells).toHaveLength(2);
    expect(cells[0].querySelector("code")?.textContent).toBe("csv|json");
    expect(cells[1].querySelector("a")?.getAttribute("href")).toBe("/rules");
    expect(cells[1].querySelector("em")?.textContent).toBe("it");
    const plain = render(<Markdown text="[Rules](/rules)" />);
    expect(plain.container.querySelector("a")).toBeNull();
    expect(plain.container.textContent).toContain("[Rules](/rules)");
  });
});

function renderHelp(initial = "/help") {
  return render(
    <I18nProvider>
      <MemoryRouter initialEntries={[initial]}>
        <Routes>
          <Route path="/help" element={<HelpPage />} />
          <Route path="/rules" element={<PageHeader title="Rules" />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

describe("HelpPage", () => {
  it("renders every section, fills live facts and filters by search", async () => {
    const { container } = renderHelp();
    expect(within(container).getAllByRole("heading", { level: 2 })).toHaveLength(HELP_SECTIONS.length);
    expect((await within(container).findAllByText(/9\.9\.9/)).length).toBeGreaterThan(0);
    expect(within(container).getAllByText(/list-2026-09/).length).toBeGreaterThan(0);

    const filtered = renderHelp("/help?q=OrphanRetentionHours");
    const count = within(filtered.container).getByTestId("help-count");
    expect(count.textContent).toMatch(/1 /);
    expect(within(filtered.container).getAllByRole("heading", { level: 2 })).toHaveLength(1);
  });

  it("puts a contextual ? button on page headers that leads to the right section", () => {
    const { container } = renderHelp("/rules");
    const button = within(container).getByTestId("help-button");
    expect(button.getAttribute("href")).toBe("/help#rules");
  });
});
