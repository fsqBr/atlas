import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type AccessView } from "../api";
import { useI18n } from "../i18n";

/** Sharing inside the tenant: open by default, restricted once someone is listed; owners and admins manage. */
export function AccessCard({ assessmentId, authEnabled }: { assessmentId: string; authEnabled: boolean }) {
  const { t, formatDate } = useI18n();
  const [view, setView] = useState<AccessView | null>(null);
  const [subject, setSubject] = useState("");
  const [subjectName, setSubjectName] = useState("");
  const [role, setRole] = useState("Viewer");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    api.getAccess(assessmentId).then(setView).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [assessmentId]);

  useEffect(() => {
    load();
  }, [load]);

  if (!authEnabled || !view) return null;

  async function grant(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setView(await api.grantAccess(assessmentId, { subject: subject.trim(), subjectName: subjectName.trim() || null, role }));
      setSubject("");
      setSubjectName("");
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function remove(entryId: string) {
    setBusy(true);
    setError(null);
    try {
      setView(await api.revokeAccess(assessmentId, entryId));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="card">
      <h2>
        {t("access.title")} {view.restricted && <span className="tag">{t("access.badge")}</span>}
      </h2>
      <p className="muted small">{view.restricted ? t("access.restricted") : t("access.open")}</p>
      {view.entries.length > 0 && (
        <ul className="access-list">
          {view.entries.map((e) => (
            <li key={e.id}>
              <span className="mono small">{e.subjectName ? `${e.subjectName} · ` : ""}{e.subject}</span>
              <span className="tag">{t(`access.role.${e.role}` as "access.role.Viewer")}</span>
              <span className="muted small">
                {t("access.grantedBy")} {e.grantedBy} · {formatDate(e.grantedAtUtc)}
              </span>
              {view.canManage && (
                <button type="button" className="button small danger" disabled={busy} onClick={() => remove(e.id)}>
                  {t("access.remove")}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {view.canManage && (
        <form className="discover-row" onSubmit={grant}>
          <label style={{ flex: 2 }}>
            <span>{t("access.subject")}</span>
            <input className="mono" value={subject} onChange={(e) => setSubject(e.target.value)} placeholder="maria@empresa.com" required />
          </label>
          <label style={{ flex: 1 }}>
            <span>{t("access.subjectName")}</span>
            <input value={subjectName} onChange={(e) => setSubjectName(e.target.value)} />
          </label>
          <label>
            <span>{t("access.role")}</span>
            <select value={role} onChange={(e) => setRole(e.target.value)}>
              {["Viewer", "Editor", "Owner"].map((r) => (
                <option key={r} value={r}>
                  {t(`access.role.${r}` as "access.role.Viewer")}
                </option>
              ))}
            </select>
          </label>
          <button type="submit" className="button" disabled={busy || subject.trim().length === 0} style={{ alignSelf: "end" }}>
            {t("access.add")}
          </button>
        </form>
      )}
      <p className="muted small">{t("access.hint")}</p>
      {!view.canEdit && <p className="banner small">{t("access.readOnly")}</p>}
      {error && <p className="error small">{error}</p>}
    </section>
  );
}
