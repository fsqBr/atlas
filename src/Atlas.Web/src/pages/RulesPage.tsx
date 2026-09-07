import { useEffect, useMemo, useState } from "react";
import { api, type RuleCatalogEntry } from "../api";
import { Card, EmptyState, PageHeader, Skeleton } from "../components/ui";
import { useI18n } from "../i18n";

const SEVERITIES = ["Critical", "High", "Medium", "Low", "Informational"];

/** The rule catalog in the open: what Atlas checks, how much it fires here, and the tenant's severity tuning. */
export function RulesPage() {
  const { t, lang } = useI18n();
  const [rules, setRules] = useState<RuleCatalogEntry[] | null>(null);
  const [filter, setFilter] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [savedRule, setSavedRule] = useState<string | null>(null);

  useEffect(() => {
    api.getRules(lang).then(setRules).catch((err) => setError(err instanceof Error ? err.message : String(err)));
  }, [lang]);

  const visible = useMemo(() => {
    if (!rules) return null;
    const needle = filter.trim().toLowerCase();
    return needle
      ? rules.filter((r) => r.id.toLowerCase().includes(needle) || r.title.toLowerCase().includes(needle) || r.category.toLowerCase().includes(needle))
      : rules;
  }, [rules, filter]);

  async function tune(rule: RuleCatalogEntry, severity: string) {
    const value = severity === rule.defaultSeverity ? null : severity;
    try {
      await api.setRuleSeverity(rule.id, value);
      setRules((current) => current?.map((r) => (r.id === rule.id ? { ...r, overrideSeverity: value } : r)) ?? null);
      setSavedRule(rule.id);
      setTimeout(() => setSavedRule((s) => (s === rule.id ? null : s)), 2500);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div>
      <PageHeader title={t("catalog.title")} subtitle={t("catalog.intro")} />
      {error && <p className="error">{error}</p>}
      <Card>
        <div className="actions" style={{ marginBottom: "0.8rem" }}>
          <input type="search" placeholder={t("catalog.search")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ maxWidth: "22rem" }} />
          <span className="muted small">{t("catalog.hint")}</span>
        </div>
        {!visible && !error && <Skeleton count={8} />}
        {visible && visible.length === 0 && <EmptyState title={t("catalog.empty")} />}
        {visible && visible.length > 0 && (
          <div style={{ overflowX: "auto" }}>
            <table>
              <thead>
                <tr>
                  <th>{t("catalog.rule")}</th>
                  <th>{t("catalog.category")}</th>
                  <th>{t("catalog.severity")}</th>
                  <th className="num">{t("catalog.open")}</th>
                  <th className="num">{t("catalog.assessments")}</th>
                </tr>
              </thead>
              <tbody>
                {visible.map((rule) => (
                  <tr key={rule.id}>
                    <td>
                      <strong>{rule.title}</strong>
                      <div className="muted small">
                        <code>{rule.id}</code> · {rule.scannerId}
                      </div>
                      <div className="muted small">{rule.description}</div>
                    </td>
                    <td>{rule.category}</td>
                    <td>
                      <select value={rule.overrideSeverity ?? rule.defaultSeverity} onChange={(e) => tune(rule, e.target.value)} title={t("catalog.hint")}>
                        {SEVERITIES.map((s) => (
                          <option key={s} value={s}>
                            {s}
                            {s === rule.defaultSeverity ? ` (${t("catalog.default")})` : ""}
                          </option>
                        ))}
                      </select>
                      {rule.overrideSeverity && (
                        <button className="link small" onClick={() => tune(rule, rule.defaultSeverity)} style={{ marginLeft: "0.4rem" }}>
                          {t("catalog.reset")}
                        </button>
                      )}
                      {savedRule === rule.id && <span className="muted small"> ✓ {t("catalog.saved")}</span>}
                    </td>
                    <td className="num">{rule.openFindings}</td>
                    <td className="num">{rule.assessments}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}
