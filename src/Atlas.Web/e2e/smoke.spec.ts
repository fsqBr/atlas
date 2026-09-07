import { expect, test, type Page } from "@playwright/test";

/**
 * Browser smoke against a running stack (BASE_URL, default http://localhost:3000). No auth, fresh or existing data.
 * Every page must render without the API error banner and without JavaScript errors; the AI estate tabs must switch;
 * a budget can be created from the UI; the shell must be served uncacheable so upgrades show up.
 */
const pages: { path: string; heading: RegExp }[] = [
  { path: "/", heading: /Portfolio dashboard|Painel/i },
  { path: "/assessments", heading: /Assessments|Avalia/i },
  { path: "/portfolio", heading: /Portfolio|Portfólio/i },
  { path: "/rules", heading: /Rule catalog|Catálogo/i },
  { path: "/compare", heading: /Compare|Comparar/i },
  { path: "/jobs", heading: /Job queue|Fila de jobs/i },
  { path: "/new", heading: /New assessment|Nova avalia/i },
  { path: "/ai-estate", heading: /AI estate/i },
  { path: "/ai-estate?tab=budgets", heading: /AI estate/i },
  { path: "/ai-estate?tab=usage", heading: /AI estate/i },
  { path: "/ai-estate?tab=spend", heading: /AI estate/i },
  { path: "/ai-estate?tab=allowlist", heading: /AI estate/i },
  { path: "/credentials", heading: /Credentials|Credenciais/i },
  { path: "/settings/tokens", heading: /API tokens|Tokens/i },
  { path: "/settings/ai", heading: /AI/i },
  { path: "/settings/cost", heading: /Cost|Custo/i },
  { path: "/settings/admin", heading: /Administration|Administração/i },
  { path: "/help", heading: /Help|Ajuda/i },
  { path: "/help?q=budget", heading: /Help|Ajuda/i },
];

async function collectErrors(page: Page): Promise<string[]> {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(`pageerror: ${e.message}`));
  page.on("console", (m) => {
    if (m.type() === "error" && !/favicon|net::ERR_ABORTED/i.test(m.text())) errors.push(`console: ${m.text()}`);
  });
  return errors;
}

for (const p of pages) {
  test(`renders ${p.path}`, async ({ page }) => {
    const errors = await collectErrors(page);
    await page.goto(p.path);
    await expect(page.getByRole("heading", { level: 1 })).toContainText(p.heading);
    await expect(page.locator("p.error", { hasText: /Could not load data from the API|Não foi possível carregar/ })).toHaveCount(0);
    await page.waitForTimeout(500);
    expect(errors, errors.join("\n")).toEqual([]);
  });
}

test("AI estate tabs switch and stay in the URL", async ({ page }) => {
  await page.goto("/ai-estate");
  await page.getByRole("tab", { name: /Budgets|Orçamentos/ }).click();
  await expect(page).toHaveURL(/tab=budgets/);
  await expect(page.getByText(/Budgets & alerts|Orçamentos e alertas/)).toBeVisible();
  await page.getByRole("tab", { name: /Developer usage|Uso por dev/ }).click();
  await expect(page).toHaveURL(/tab=usage/);
  await expect(page.getByText(/Developer usage|Uso por desenvolvedor/).first()).toBeVisible();
});

test("a budget can be created from the UI and shows its bar", async ({ page }) => {
  await page.goto("/ai-estate?tab=budgets");
  const name = `e2e budget ${Date.now()}`;
  await page.getByPlaceholder(/Q4 AI budget|orçamento de IA/).fill(name);
  await page.getByPlaceholder("500").fill("1234");
  await page.getByRole("button", { name: /Save budget|Salvar orçamento/ }).click();
  await expect(page.getByText(name)).toBeVisible();
  await expect(page.getByRole("progressbar", { name: name })).toBeVisible();
});

test("theme toggle switches data-theme", async ({ page }) => {
  await page.goto("/?theme=dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await page.goto("/?theme=light");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
});

test("the shell is served uncacheable and assets immutable", async ({ request, baseURL }) => {
  const shell = await request.get(baseURL! + "/ai-estate");
  expect(shell.headers()["cache-control"]).toMatch(/no-cache/);
  const html = await shell.text();
  const asset = html.match(/assets\/[^"']+\.js/)?.[0];
  expect(asset).toBeTruthy();
  const js = await request.get(`${baseURL}/${asset}`);
  expect(js.headers()["cache-control"]).toMatch(/immutable/);
});

test("API is reachable through the web proxy", async ({ request, baseURL }) => {
  const r = await request.get(baseURL! + "/api/version");
  expect(r.ok()).toBeTruthy();
  expect((await r.json()).version).toMatch(/^\d+\.\d+\.\d+/);
});

test("the ? button opens the manual at the section for the page, and search narrows it", async ({ page }) => {
  await page.goto("/rules");
  await page.getByTestId("help-button").click();
  await expect(page).toHaveURL(/\/help#rules$/);
  await expect(page.locator("#help-rules h2")).toBeVisible();
  await page.getByRole("searchbox").fill("OrphanRetentionHours");
  await expect(page.getByTestId("help-count")).toContainText("1");
  await expect(page.locator("section.help-section")).toHaveCount(1);
});
