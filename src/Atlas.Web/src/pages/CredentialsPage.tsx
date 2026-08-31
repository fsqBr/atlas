import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type Credential } from "../api";
import { ErrorBox, Spinner } from "../components";
import { useI18n } from "../i18n";

export function CredentialsPage() {
  const { t, formatDate } = useI18n();
  const [configured, setConfigured] = useState(true);
  const [items, setItems] = useState<Credential[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [username, setUsername] = useState("");
  const [secret, setSecret] = useState("");
  const [description, setDescription] = useState("");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const load = useCallback(() => {
    api
      .listCredentials()
      .then((r) => {
        setConfigured(r.configured);
        setItems(r.items);
        setError(null);
      })
      .catch(() => setError(t("error.load")));
  }, [t]);

  useEffect(() => {
    load();
  }, [load]);

  const validName = /^[A-Za-z0-9._-]{1,100}$/.test(name);
  const canSave = validName && secret.trim().length > 0 && !saving && configured;

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!canSave) return;
    setSaving(true);
    setFormError(null);
    setSaved(false);
    try {
      await api.upsertCredential(name, {
        secret: secret.trim(),
        username: username.trim() || null,
        description: description.trim() || null,
      });
      setSecret("");
      setSaved(true);
      load();
    } catch (err) {
      setFormError(`${t("cred.error")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setSaving(false);
    }
  }

  async function remove(credential: Credential) {
    if (!window.confirm(t("cred.confirmDelete"))) return;
    try {
      await api.deleteCredential(credential.name);
      load();
    } catch (err) {
      setError(err instanceof Error && err.message.includes("used by") ? t("cred.inUse") : String(err));
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>{t("cred.title")}</h1>
      </div>
      <p className="muted">{t("cred.intro")}</p>

      {!configured && <ErrorBox message={t("cred.notConfigured")} />}
      {error && <ErrorBox message={error} />}

      <form className="card form" onSubmit={save}>
        <label>
          <span>{t("cred.name")}</span>
          <input
            className="mono"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={t("cred.namePlaceholder")}
            required
          />
          <small className="muted">{t("cred.nameHint")}</small>
        </label>
        <label>
          <span>{t("cred.username")}</span>
          <input className="mono" value={username} onChange={(e) => setUsername(e.target.value)} />
          <small className="muted">{t("cred.usernameHint")}</small>
        </label>
        <label>
          <span>{t("cred.secret")}</span>
          <input
            className="mono"
            type="password"
            autoComplete="off"
            value={secret}
            onChange={(e) => setSecret(e.target.value)}
            required
          />
        </label>
        <label>
          <span>{t("cred.description")}</span>
          <input value={description} onChange={(e) => setDescription(e.target.value)} />
        </label>

        {formError && <ErrorBox message={formError} />}
        {saved && <p className="banner ok">{t("cred.saved")}</p>}

        <div className="actions">
          <button type="submit" className="button primary" disabled={!canSave}>
            {saving ? t("cred.saving") : t("cred.save")}
          </button>
        </div>
      </form>

      <div className="card">
        {items === null ? (
          <Spinner />
        ) : items.length === 0 ? (
          <p className="muted">{t("cred.empty")}</p>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>{t("cred.name")}</th>
                <th>{t("cred.username")}</th>
                <th>{t("cred.description")}</th>
                <th>{t("cred.usedBy")}</th>
                <th>{t("cred.updated")}</th>
                <th>{t("cred.lastUsed")}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {items.map((c) => (
                <tr key={c.name}>
                  <td className="mono">{c.name}</td>
                  <td className="mono">{c.username ?? "—"}</td>
                  <td>{c.description ?? "—"}</td>
                  <td>
                    {c.usedByAssessments} {t("cred.assessments")}
                  </td>
                  <td>{formatDate(c.updatedAtUtc)}</td>
                  <td>{c.lastUsedAtUtc ? formatDate(c.lastUsedAtUtc) : t("cred.never")}</td>
                  <td>
                    <button
                      type="button"
                      className="button small danger"
                      disabled={c.usedByAssessments > 0}
                      title={c.usedByAssessments > 0 ? t("cred.inUse") : undefined}
                      onClick={() => remove(c)}
                    >
                      {t("cred.delete")}
                    </button>
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
