import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type ApiToken, type ApiTokenCreated } from "../api";
import { ErrorBox, Spinner } from "../components";
import { useI18n } from "../i18n";

/** Service tokens for CI and scripts: created once, shown once, revocable. */
export function ApiTokensPage() {
  const { t, formatDate } = useI18n();
  const [items, setItems] = useState<ApiToken[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [role, setRole] = useState("analyst");
  const [expiresIn, setExpiresIn] = useState("365");
  const [saving, setSaving] = useState(false);
  const [created, setCreated] = useState<ApiTokenCreated | null>(null);
  const [copied, setCopied] = useState(false);
  const [authEnabled, setAuthEnabled] = useState(true);

  const load = useCallback(() => {
    api
      .listTokens()
      .then((r) => {
        setItems(r);
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    api.authConfig().then((c) => setAuthEnabled(c.enabled)).catch(() => setAuthEnabled(true));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  async function create(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    setCreated(null);
    setCopied(false);
    try {
      const days = Number(expiresIn);
      const expiresAtUtc = days > 0 ? new Date(Date.now() + days * 86400000).toISOString() : null;
      setCreated(await api.createToken({ name: name.trim(), role, expiresAtUtc }));
      setName("");
      load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function revoke(token: ApiToken) {
    if (!window.confirm(t("tokens.confirmRevoke", { name: token.name }))) return;
    try {
      await api.revokeToken(token.id);
      load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function copy() {
    if (!created) return;
    try {
      await navigator.clipboard.writeText(created.secret);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>{t("tokens.title")}</h1>
      </div>
      <p className="muted">{t("tokens.intro")}</p>
      {!authEnabled && <p className="banner small">{t("tokens.authOff")}</p>}
      {error && <ErrorBox message={error} />}

      <form className="card form" onSubmit={create}>
        <label>
          <span>{t("tokens.name")}</span>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="github-actions · atlas-gate" maxLength={100} required />
        </label>
        <div className="discover-row">
          <label>
            <span>{t("tokens.role")}</span>
            <select value={role} onChange={(e) => setRole(e.target.value)}>
              <option value="analyst">{t("tokens.role.analyst")}</option>
              <option value="admin">{t("tokens.role.admin")}</option>
            </select>
          </label>
          <label>
            <span>{t("tokens.expires")}</span>
            <select value={expiresIn} onChange={(e) => setExpiresIn(e.target.value)}>
              <option value="30">30 {t("tokens.days")}</option>
              <option value="90">90 {t("tokens.days")}</option>
              <option value="365">365 {t("tokens.days")}</option>
              <option value="0">{t("tokens.never")}</option>
            </select>
          </label>
        </div>
        <div className="actions">
          <button type="submit" className="button primary" disabled={saving || name.trim().length === 0}>
            {saving ? t("tokens.creating") : t("tokens.create")}
          </button>
        </div>
        {created && (
          <div className="banner ok">
            <strong>{t("tokens.createdTitle")}</strong>
            <p className="small">{t("tokens.createdHint")}</p>
            <code className="mono token-secret">{created.secret}</code>
            <div className="actions">
              <button type="button" className="button small" onClick={copy}>
                {copied ? t("tokens.copied") : t("tokens.copy")}
              </button>
            </div>
            <p className="small muted mono">curl -H "Authorization: Bearer {created.secret.slice(0, 16)}…" {window.location.origin}/api/assessments</p>
          </div>
        )}
      </form>

      <div className="card">
        {items === null ? (
          <Spinner />
        ) : items.length === 0 ? (
          <p className="muted">{t("tokens.empty")}</p>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>{t("tokens.name")}</th>
                <th>{t("tokens.hint")}</th>
                <th>{t("tokens.role")}</th>
                <th>{t("tokens.createdBy")}</th>
                <th>{t("tokens.created")}</th>
                <th>{t("tokens.expires")}</th>
                <th>{t("tokens.lastUsed")}</th>
                <th>{t("tokens.status")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {items.map((tk) => (
                <tr key={tk.id} className={tk.active ? "" : "muted"}>
                  <td>
                    <strong>{tk.name}</strong>
                  </td>
                  <td className="mono small">{tk.hint}</td>
                  <td>{t(`tokens.role.${tk.role}` as "tokens.role.analyst")}</td>
                  <td className="small">{tk.createdBy}</td>
                  <td className="small">{formatDate(tk.createdAtUtc)}</td>
                  <td className="small">{tk.expiresAtUtc ? formatDate(tk.expiresAtUtc) : t("tokens.never")}</td>
                  <td className="small">{tk.lastUsedAtUtc ? formatDate(tk.lastUsedAtUtc) : "—"}</td>
                  <td>{tk.revokedAtUtc ? t("tokens.revoked") : tk.active ? t("tokens.active") : t("tokens.expired")}</td>
                  <td>
                    {tk.active && (
                      <button type="button" className="button small danger" onClick={() => revoke(tk)}>
                        {t("tokens.revoke")}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}
