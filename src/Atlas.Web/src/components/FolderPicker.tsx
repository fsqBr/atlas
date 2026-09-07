import { useEffect, useState } from "react";
import { api, type BrowseResult } from "../api";
import { ErrorBox, Spinner } from "../components";
import { useI18n } from "../i18n";

/**
 * File-dialog over the source roots mounted into the containers: navigate folder by
 * folder (one level per request, never a whole tree) and pick one. Folders with
 * .NET projects and git repositories are marked.
 */
export function FolderPicker({ value, onChange }: { value: string; onChange: (path: string, name: string) => void }) {
  const { t } = useI18n();
  const [data, setData] = useState<BrowseResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [loading, setLoading] = useState(false);

  function browse(path?: string) {
    setLoading(true);
    setError(null);
    api
      .browseLocal(path)
      .then((r) => {
        setData(r);
        setFilter("");
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false));
  }

  useEffect(() => {
    browse(undefined);
  }, []);

  if (error) return <ErrorBox message={error} />;
  if (!data) return <Spinner />;

  const entries = data.entries.filter((e) => !filter || e.name.toLowerCase().includes(filter.toLowerCase()));
  const currentName = data.current ? data.current.split("/").filter(Boolean).pop() ?? data.current : "";

  return (
    <div className="picker">
      <div className="picker-head">
        <div className="picker-roots">
          {data.roots.map((r) => (
            <button
              key={r.path}
              type="button"
              className={`button small ${data.current?.startsWith(r.path) ? "primary" : ""}`}
              disabled={!r.exists}
              title={r.exists ? r.path : t("picker.rootMissing")}
              onClick={() => browse(r.path)}
            >
              {r.label}
            </button>
          ))}
        </div>
        {data.current && (
          <div className="picker-path mono small">
            <button type="button" className="button small" disabled={!data.parent} onClick={() => browse(data.parent ?? undefined)}>
              ↑
            </button>{" "}
            {data.current}
          </div>
        )}
        {data.current && <input className="search" placeholder={t("picker.filter")} value={filter} onChange={(e) => setFilter(e.target.value)} />}
      </div>

      {!data.current && <p className="muted small">{t("picker.chooseRoot")}</p>}
      {data.roots.every((r) => !r.exists) && <p className="muted small">{t("picker.noRoots")}</p>}

      {data.current && (
        <ul className="picker-list">
          {loading && (
            <li className="muted small">
              <Spinner />
            </li>
          )}
          {!loading && entries.length === 0 && <li className="muted small">{t("picker.empty")}</li>}
          {!loading &&
            entries.map((e) => (
              <li key={e.path} className={value === e.path ? "selected" : ""}>
                <button type="button" className="picker-entry" onClick={() => browse(e.path)} title={t("picker.open")}>
                  📁 {e.name}
                  {e.hasDotNetProjects && <span className="tag">.NET</span>}
                  {e.hasSolution && <span className="tag">sln</span>}
                  {e.isGitRepo && <span className="tag">git</span>}
                </button>
                <button type="button" className="button small" onClick={() => onChange(e.path, e.name)}>
                  {t("picker.select")}
                </button>
              </li>
            ))}
        </ul>
      )}

      {data.current && (
        <div className="actions">
          <button type="button" className="button primary" onClick={() => onChange(data.current!, currentName)}>
            {t("picker.selectCurrent", { name: currentName })}
          </button>
          {value && (
            <span className="muted small mono">
              {t("picker.selected")}: {value}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
