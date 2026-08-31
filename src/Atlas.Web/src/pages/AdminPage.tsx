import { useEffect, useState } from "react";
import { api, type NotificationSettings, type Tenant } from "../api";
import { Card, PageHeader, Skeleton } from "../components/ui";
import { useI18n } from "../i18n";

const DAYS = ["", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

/** Administration: tenants and this tenant's notification channels. Server-side admin role required for writes. */
export function AdminPage() {
  const { t } = useI18n();
  const [tenants, setTenants] = useState<Tenant[] | null>(null);
  const [newTenant, setNewTenant] = useState({ name: "", externalKey: "" });
  const [settings, setSettings] = useState<NotificationSettings | null>(null);
  const [draft, setDraft] = useState<NotificationSettings | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [secretTouched, setSecretTouched] = useState(false);

  async function load() {
    try {
      setTenants(await api.listTenants());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
    try {
      const s = await api.getNotificationSettings();
      setSettings(s);
      setDraft(s);
    } catch {
      setSettings(null);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  async function createTenant() {
    setError(null);
    try {
      await api.createTenant({ name: newTenant.name.trim(), externalKey: newTenant.externalKey.trim() || null });
      setNewTenant({ name: "", externalKey: "" });
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function saveSettings() {
    if (!draft) return;
    setError(null);
    setMessage(null);
    try {
      // Secret is write-only: null = keep what is stored; "" = clear. Only send a value when the
      // admin actually touched the field.
      const hour = Number.isFinite(draft.digestHourUtc) ? Math.min(23, Math.max(0, draft.digestHourUtc)) : 13;
      await api.setNotificationSettings({ ...draft, digestHourUtc: hour, secret: secretTouched ? (draft.secret ?? "") : null });
      setMessage(t("admin.saved"));
      setSecretTouched(false);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div>
      <PageHeader title={t("admin.title")} subtitle={t("admin.intro")} />
      {error && <p className="error">{error}</p>}

      <Card title={t("admin.tenants")} subtitle={t("admin.tenantsHint")}>
        {!tenants ? (
          <Skeleton count={3} />
        ) : (
          <>
            <table>
              <thead>
                <tr>
                  <th>{t("admin.tenantName")}</th>
                  <th>{t("admin.externalKey")}</th>
                  <th>{t("detail.created")}</th>
                </tr>
              </thead>
              <tbody>
                {tenants.map((tenant) => (
                  <tr key={tenant.id}>
                    <td>
                      {tenant.name}
                      {tenant.isDefault && <span className="tag" style={{ marginLeft: "0.4rem" }}>{t("admin.default")}</span>}
                    </td>
                    <td className="mono small">{tenant.externalKey ?? "—"}</td>
                    <td className="small">{tenant.createdAtUtc.slice(0, 10)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="discover-row" style={{ marginTop: "0.8rem" }}>
              <input placeholder={t("admin.tenantName")} value={newTenant.name} onChange={(e) => setNewTenant({ ...newTenant, name: e.target.value })} />
              <input className="mono" placeholder={t("admin.externalKeyPh")} value={newTenant.externalKey} onChange={(e) => setNewTenant({ ...newTenant, externalKey: e.target.value })} />
              <button className="button primary" disabled={!newTenant.name.trim()} onClick={() => void createTenant()}>
                {t("admin.addTenant")}
              </button>
            </div>
          </>
        )}
      </Card>

      <Card title={t("admin.notifications")} subtitle={t("admin.notificationsHint")}>
        {!draft ? (
          <Skeleton count={4} />
        ) : (
          <div className="form" style={{ maxWidth: "44rem" }}>
            <label>
              <span>{t("detail.webhook")}</span>
              <input className="mono" value={draft.webhookUrl ?? ""} onChange={(e) => setDraft({ ...draft, webhookUrl: e.target.value || null })} placeholder="https://hooks.example.com/atlas" />
            </label>
            <label>
              <span>{t("admin.secret")}{settings?.secretSet && <em className="muted"> · {t("admin.secretStored")}</em>}</span>
              <input
                className="mono"
                value={draft.secret ?? ""}
                onChange={(e) => {
                  setSecretTouched(true);
                  setDraft({ ...draft, secret: e.target.value });
                }}
                placeholder={settings?.secretSet ? t("admin.secretStoredPh") : t("admin.secretPh")}
              />
            </label>
            <div className="discover-row">
              <label style={{ flex: 1 }}>
                <span>Slack</span>
                <input className="mono" value={draft.slackWebhookUrl ?? ""} onChange={(e) => setDraft({ ...draft, slackWebhookUrl: e.target.value || null })} placeholder="https://hooks.slack.com/services/…" />
              </label>
              <label style={{ flex: 1 }}>
                <span>Teams</span>
                <input className="mono" value={draft.teamsWebhookUrl ?? ""} onChange={(e) => setDraft({ ...draft, teamsWebhookUrl: e.target.value || null })} placeholder="https://…logic.azure.com/workflows/…" />
              </label>
            </div>
            <div className="discover-row">
              <label>
                <span>{t("admin.digestDay")}</span>
                <select value={draft.digestDayOfWeek ?? ""} onChange={(e) => setDraft({ ...draft, digestDayOfWeek: e.target.value || null })}>
                  {DAYS.map((d) => (
                    <option key={d} value={d}>{d === "" ? t("admin.digestOff") : d}</option>
                  ))}
                </select>
              </label>
              <label>
                <span>{t("admin.digestHour")}</span>
                <input type="number" min={0} max={23} style={{ maxWidth: "5rem" }} value={Number.isFinite(draft.digestHourUtc) ? draft.digestHourUtc : ""} onChange={(e) => setDraft({ ...draft, digestHourUtc: e.target.value === "" ? Number.NaN : Number(e.target.value) })} />
              </label>
            </div>
            <p className="muted small" style={{ margin: 0 }}>
              {settings?.isDefault ? t("admin.usingGlobal") : t("admin.overriding")}
            </p>
            <div className="actions">
              <button className="button primary" onClick={() => void saveSettings()}>{t("cost.save")}</button>
              {message && <span className="muted small">{message}</span>}
            </div>
          </div>
        )}
      </Card>
    </div>
  );
}
