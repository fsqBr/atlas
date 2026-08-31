import { useEffect, useState, type ReactNode } from "react";
import { Link, NavLink, Route, Routes, useLocation } from "react-router-dom";
import { api, type Me } from "./api";
import { currentUser, isAuthEnabled, signOut } from "./auth";
import { useI18n } from "./i18n";
import { useTheme, type Theme } from "./theme";
import { AssessmentsPage } from "./pages/AssessmentsPage";
import { DashboardPage } from "./pages/DashboardPage";
import { NewAssessmentPage } from "./pages/NewAssessmentPage";
import { AssessmentDetailPage } from "./pages/AssessmentDetailPage";
import { AiSettingsPage } from "./pages/AiSettingsPage";
import { ApiTokensPage } from "./pages/ApiTokensPage";
import { CredentialsPage } from "./pages/CredentialsPage";
import { ComparePage } from "./pages/ComparePage";
import { PortfolioPage } from "./pages/PortfolioPage";
import { JobsPage } from "./pages/JobsPage";
import { RulesPage } from "./pages/RulesPage";
import { CostSettingsPage } from "./pages/CostSettingsPage";

export function App() {
  const { t, lang, setLang } = useI18n();
  const [me, setMe] = useState<Me | null>(null);
  const [open, setOpen] = useState(false);
  const [theme, setTheme] = useTheme();
  const location = useLocation();
  const [present, setPresent] = useState(false);
  const [version, setVersion] = useState<string | null>(null);

  useEffect(() => {
    api.getVersion().then((v) => setVersion(v.version)).catch(() => setVersion(null));
  }, []);

  useEffect(() => {
    document.documentElement.classList.toggle("present", present);
    if (!present) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setPresent(false);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [present]);

  useEffect(() => {
    const onPresent = () => setPresent(true);
    window.addEventListener("atlas-present", onPresent);
    return () => window.removeEventListener("atlas-present", onPresent);
  }, []);

  useEffect(() => {
    api.me().then(setMe).catch(() => setMe(null));
  }, []);

  useEffect(() => setOpen(false), [location.pathname]);

  const item = (to: string, icon: string, label: ReactNode, end = false) => (
    <NavLink to={to} end={end}>
      <span className="ico" aria-hidden>{icon}</span>
      {label}
    </NavLink>
  );

  return (
    <div className="app">
      {open && <div className="scrim" onClick={() => setOpen(false)} />}
      <aside className={`sidebar${open ? " open" : ""}`}>
        <Link to="/" className="brand">
          <span className="brand-mark">◈</span>
          <span>
            <strong>{t("app.title")}</strong>
            <small>{t("app.subtitle")}</small>
          </span>
        </Link>
        <nav className="side-nav" aria-label="Main">
          <div className="side-group">{t("nav.group.overview")}</div>
          {item("/", "◈", t("nav.dashboard"), true)}
          {item("/assessments", "▤", t("nav.assessments"))}
          {item("/portfolio", "◔", t("nav.portfolio"))}
          <div className="side-group">{t("nav.group.analysis")}</div>
          {item("/compare", "⇄", t("nav.compare"))}
          {item("/jobs", "≣", t("nav.jobs"))}
          {item("/rules", "§", t("nav.rules"))}
          <div className="side-group">{t("nav.group.settings")}</div>
          {item("/credentials", "⚿", t("nav.credentials"))}
          {item("/settings/ai", "✨", t("nav.ai"))}
          {item("/settings/cost", "¤", t("nav.cost"))}
          {item("/settings/tokens", "⌗", t("nav.tokens"))}
        </nav>
        <div className="side-cta">
          <Link to="/new" className="button primary">＋ {t("nav.new")}</Link>
        </div>
        <div className="side-foot">
          {me && !me.isDefaultTenant && me.tenantName && (
            <span className="tag tenant" title={me.tenantId ?? ""}>{me.tenantName}</span>
          )}
          {isAuthEnabled() && currentUser() && (
            <div className="row">
              <span className="muted small">{currentUser()!.name}</span>
              <button type="button" className="button small" onClick={() => void signOut()}>
                {t("auth.signOut")}
              </button>
            </div>
          )}
          <div className="row">
            <button type="button" className="lang" onClick={() => setLang(lang === "en" ? "pt-BR" : "en")} aria-label="Change language">
              {t("lang.toggle")}
            </button>
            <label className="theme-pick" title={t("theme.label")}>
              <span aria-hidden>◐</span>
              <select value={theme} onChange={(e) => setTheme(e.target.value as Theme)} aria-label={t("theme.label")}>
                <option value="system">{t("theme.system")}</option>
                <option value="light">{t("theme.light")}</option>
                <option value="dark">{t("theme.dark")}</option>
              </select>
            </label>
          </div>
          {version && (
            <a
              className="muted small"
              href="https://github.com/fsqBr/atlas/releases"
              target="_blank"
              rel="noreferrer"
              title={t("version.releases")}
              style={{ padding: "0.15rem 0.75rem", textDecoration: "none" }}
            >
              Atlas v{version}
            </a>
          )}
        </div>
      </aside>
      {present && (
        <button type="button" className="button present-exit" onClick={() => setPresent(false)}>
          ✕ {t("present.exit")}
        </button>
      )}

      <div className="main">
        <div className="mobile-bar">
          <button type="button" className="menu-btn" onClick={() => setOpen(true)} aria-label={t("nav.menu")}>☰</button>
          <Link to="/" className="brand" style={{ padding: 0 }}>
            <span className="brand-mark">◈</span>
            <strong>{t("app.title")}</strong>
          </Link>
        </div>
        <main className="page">
          <Routes>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/assessments" element={<AssessmentsPage />} />
            <Route path="/new" element={<NewAssessmentPage />} />
            <Route path="/credentials" element={<CredentialsPage />} />
            <Route path="/settings/ai" element={<AiSettingsPage />} />
            <Route path="/settings/cost" element={<CostSettingsPage />} />
            <Route path="/settings/tokens" element={<ApiTokensPage />} />
            <Route path="/portfolio" element={<PortfolioPage />} />
            <Route path="/compare" element={<ComparePage />} />
            <Route path="/jobs" element={<JobsPage />} />
            <Route path="/rules" element={<RulesPage />} />
            <Route path="/assessments/:id" element={<AssessmentDetailPage />} />
          </Routes>
        </main>
      </div>
    </div>
  );
}
