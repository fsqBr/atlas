import { useEffect, useState } from "react";
import { api, type CostProfile } from "../api";
import { Card, PageHeader, Skeleton } from "../components/ui";
import { useI18n } from "../i18n";

const CURRENCIES = ["BRL", "USD", "EUR", "GBP", "CAD", "AUD", "CHF", "MXN", "COP", "ARS"];

/** The tenant's market parameters for the cost model: an hourly rate is a market fact, not an FX conversion. */
export function CostSettingsPage() {
  const { t } = useI18n();
  const [profile, setProfile] = useState<CostProfile | null>(null);
  const [currency, setCurrency] = useState("BRL");
  const [rate, setRate] = useState("");
  const [team, setTeam] = useState("");
  const [hosting, setHosting] = useState("");
  const [support, setSupport] = useState("");
  const [sqlServer, setSqlServer] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const p = await api.getCostProfile();
      setProfile(p);
      setCurrency(p.currency);
      setRate(String(p.hourlyRate));
      setTeam(String(p.teamSize));
      setHosting(p.windowsHostingPerLegacyAppYear != null ? String(p.windowsHostingPerLegacyAppYear) : "");
      setSupport(p.extendedSupportPerLegacyAppYear != null ? String(p.extendedSupportPerLegacyAppYear) : "");
      setSqlServer(p.sqlServerSavingsPerYear != null ? String(p.sqlServerSavingsPerYear) : "");
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  useEffect(() => {
    void load();
  }, []);

  async function save() {
    setMessage(null);
    setError(null);
    try {
      await api.setCostProfile({
        currency,
        hourlyRate: Number(rate),
        teamSize: team ? Number(team) : null,
        author: null,
        windowsHostingPerLegacyAppYear: hosting ? Number(hosting) : null,
        extendedSupportPerLegacyAppYear: support ? Number(support) : null,
        sqlServerSavingsPerYear: sqlServer ? Number(sqlServer) : null,
      });
      setMessage(t("cost.saved"));
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function reset() {
    setMessage(null);
    setError(null);
    try {
      await api.resetCostProfile();
      setMessage(t("cost.resetDone"));
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div>
      <PageHeader title={t("cost.title")} subtitle={t("cost.intro")} />
      {error && <p className="error">{error}</p>}
      <Card title={t("cost.market")} subtitle={t("cost.marketHint")}>
        {!profile ? (
          <Skeleton count={3} />
        ) : (
          <div className="form" style={{ maxWidth: "34rem" }}>
            <div className="discover-row">
              <label>
                <span>{t("cost.currency")}</span>
                <select value={currency} onChange={(e) => setCurrency(e.target.value)}>
                  {[...new Set([currency, ...CURRENCIES])].map((c) => (
                    <option key={c} value={c}>{c}</option>
                  ))}
                </select>
              </label>
              <label>
                <span>{t("cost.hourlyRate")}</span>
                <input type="number" min={1} value={rate} onChange={(e) => setRate(e.target.value)} style={{ maxWidth: "8rem" }} />
              </label>
              <label>
                <span>{t("cost.teamSize")}</span>
                <input type="number" min={1} max={500} value={team} onChange={(e) => setTeam(e.target.value)} style={{ maxWidth: "6rem" }} />
              </label>
            </div>
            <p className="muted small" style={{ margin: 0 }}>
              {profile.isDefault ? t("cost.usingDefaults") : t("cost.updatedBy", { by: profile.updatedBy ?? "?", when: profile.updatedAtUtc?.slice(0, 10) ?? "" })}
            </p>
            <div className="form" style={{ borderTop: "1px solid var(--line, #e3e8ea)", paddingTop: "0.8rem", marginTop: "0.4rem" }}>
              <p className="muted small" style={{ margin: 0 }}>{t("cost.savingsHint")}</p>
              <div className="discover-row">
                <label>
                  <span>{t("cost.savingHosting")}</span>
                  <input type="number" min={0} value={hosting} onChange={(e) => setHosting(e.target.value)} placeholder={t("cost.perLegacyAppYear")} style={{ maxWidth: "9rem" }} />
                </label>
                <label>
                  <span>{t("cost.savingSupport")}</span>
                  <input type="number" min={0} value={support} onChange={(e) => setSupport(e.target.value)} placeholder={t("cost.perLegacyAppYear")} style={{ maxWidth: "9rem" }} />
                </label>
                <label>
                  <span>{t("cost.savingSql")}</span>
                  <input type="number" min={0} value={sqlServer} onChange={(e) => setSqlServer(e.target.value)} placeholder={t("cost.perYear")} style={{ maxWidth: "9rem" }} />
                </label>
              </div>
            </div>
            <div className="actions">
              <button className="button primary" onClick={save}>{t("cost.save")}</button>
              {!profile.isDefault && (
                <button className="button" onClick={reset}>{t("cost.reset")}</button>
              )}
              {message && <span className="muted small">{message}</span>}
            </div>
          </div>
        )}
      </Card>
      <p className="muted small">{t("cost.applies")}</p>
    </div>
  );
}
