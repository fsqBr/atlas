import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api, type Credential, type DiscoveredRepository } from "../api";
import { FolderPicker } from "../components/FolderPicker";
import { filesFromInput, pickFolder, supportsDirectoryPicker, zipAndUpload, type UploadProgress } from "../upload";
import { ErrorBox } from "../components";
import { useI18n } from "../i18n";


type Kind = "local" | "upload" | "git" | "github" | "azure-devops" | "gitlab";
const PROVIDER_KINDS: Kind[] = ["github", "azure-devops", "gitlab"];

export function NewAssessmentPage() {
  const { t, formatDate } = useI18n();
  const navigate = useNavigate();

  const [name, setName] = useState("");
  const [kind, setKind] = useState<Kind>("local");
  const [folder, setFolder] = useState("");
  const [customPath, setCustomPath] = useState("");
  const [showCustom, setShowCustom] = useState(false);
  const [upload, setUpload] = useState<{ uploadId: string; name: string; bytes: number; files: number; skipped: number } | null>(null);
  const [uploadProgress, setUploadProgress] = useState<UploadProgress | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [url, setUrl] = useState("");
  const [locator, setLocator] = useState("");
  const [branch, setBranch] = useState("");
  const [excludes, setExcludes] = useState("");
  const [credentials, setCredentials] = useState<Credential[]>([]);
  const [credential, setCredential] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Provider discovery
  const [discovering, setDiscovering] = useState(false);
  const [discovered, setDiscovered] = useState<DiscoveredRepository[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());

  useEffect(() => {
    api
      .listCredentials()
      .then((r) => setCredentials(r.items))
      .catch(() => setCredentials([]));
  }, []);

  const isProvider = PROVIDER_KINDS.includes(kind);
  const singleLocator = kind === "local" ? (showCustom ? customPath.trim() : folder) : kind === "upload" ? (upload?.uploadId ?? "") : url.trim();
  const canSubmit = !submitting && (isProvider ? selected.size > 0 : name.trim().length > 0 && singleLocator.length > 0);

  const providerKey = kind === "azure-devops" ? "azureDevOps" : kind === "gitlab" ? "gitlab" : "github";

  function changeKind(next: Kind) {
    setKind(next);
    setDiscovered(null);
    setSelected(new Set());
    setError(null);
  }

  async function discover() {
    if (!locator.trim()) return;
    setDiscovering(true);
    setError(null);
    setDiscovered(null);
    try {
      const repos = await api.discover({ sourceKind: kind, locator: locator.trim(), credentialName: credential || null });
      setDiscovered(repos);
      const active = repos.filter((r) => !r.archived);
      // Preselect when the choice is obvious (one repo, or a handful of active ones).
      setSelected(new Set(repos.length === 1 ? [repos[0].locator] : active.length <= 5 ? active.map((r) => r.locator) : []));
    } catch (err) {
      setError(`${t("new.discoverError")}: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setDiscovering(false);
    }
  }

  async function chooseFolder() {
    setUploadError(null);
    setUpload(null);
    try {
      const picked = await pickFolder(setUploadProgress);
      const result = await zipAndUpload(picked, setUploadProgress);
      setUpload({ ...result, skipped: picked.skipped });
      if (!name.trim()) setName(result.name);
    } catch (err) {
      if ((err as { name?: string }).name !== "AbortError") setUploadError(err instanceof Error ? err.message : String(err));
    } finally {
      setUploadProgress(null);
    }
  }

  async function chooseFolderFromInput(list: FileList | null) {
    if (!list || list.length === 0) return;
    setUploadError(null);
    setUpload(null);
    try {
      const picked = filesFromInput(list);
      const result = await zipAndUpload(picked, setUploadProgress);
      setUpload({ ...result, skipped: picked.skipped });
      if (!name.trim()) setName(result.name);
    } catch (err) {
      setUploadError(err instanceof Error ? err.message : String(err));
    } finally {
      setUploadProgress(null);
    }
  }

  function toggle(repoLocator: string) {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(repoLocator)) next.delete(repoLocator);
      else next.add(repoLocator);
      return next;
    });
  }

  const excludePaths = excludes.split("\n").map((l) => l.trim()).filter((l) => l.length > 0 && !l.startsWith("#"));

  const selectedRepos = useMemo(() => (discovered ?? []).filter((r) => selected.has(r.locator)), [discovered, selected]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    setSubmitting(true);
    setError(null);
    try {
      if (!isProvider) {
        const created = await api.createAssessment({
          name: name.trim(),
          sourceKind: kind,
          sourceLocator: singleLocator,
          branch: kind === "git" && branch.trim() ? branch.trim() : null,
          credentialName: kind === "git" && credential ? credential : null,
          excludePaths,
        });
        navigate(`/assessments/${created.id}`);
        return;
      }

      const prefix = name.trim();
      const created: string[] = [];
      const errors: string[] = [];
      for (const repo of selectedRepos) {
        try {
          const result = await api.createAssessment({
            name: prefix ? `${prefix} — ${repo.name}` : repo.name,
            sourceKind: kind,
            sourceLocator: repo.locator,
            branch: branch.trim() || null,
            credentialName: credential || null,
            excludePaths,
          });
          created.push(result.id);
        } catch (err) {
          errors.push(`${repo.name}: ${err instanceof Error ? err.message : String(err)}`);
        }
      }

      if (errors.length > 0) {
        setError(t("new.batchPartial", { created: created.length, failed: errors.length, errors: errors.join("; ") }));
        setSubmitting(false);
        return;
      }

      navigate(created.length === 1 ? `/assessments/${created[0]}` : "/");
    } catch (err) {
      setError(`${t("new.error")}: ${err instanceof Error ? err.message : String(err)}`);
      setSubmitting(false);
    }
  }

  const credentialSelect = (hint: string) => (
    <label>
      <span>{t("new.credential")}</span>
      <select value={credential} onChange={(e) => setCredential(e.target.value)}>
        <option value="">{t("new.credentialNone")}</option>
        {credentials.map((c) => (
          <option key={c.name} value={c.name}>
            {c.name}
            {c.description ? ` · ${c.description}` : ""}
          </option>
        ))}
      </select>
      <small className="muted">
        {hint} <Link to="/credentials">{t("nav.credentials")}</Link>
      </small>
    </label>
  );

  return (
    <>
      <div className="page-head">
        <h1>{t("new.title")}</h1>
      </div>

      <form className="card form" onSubmit={submit}>
        <label>
          <span>{isProvider ? t("new.namePrefix") : t("new.name")}</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={isProvider ? "" : t("new.namePlaceholder")}
            required={!isProvider}
          />
          {isProvider && <small className="muted">{t("new.namePrefixHint")}</small>}
        </label>

        <fieldset>
          <legend>{t("new.kind")}</legend>
          {(
            [
              ["local", t("new.kind.local")],
              ["upload", t("new.kind.upload")],
              ["git", t("new.kind.git")],
              ["github", t("new.kind.github")],
              ["azure-devops", t("new.kind.azureDevOps")],
              ["gitlab", t("new.kind.gitlab")],
            ] as [Kind, string][]
          ).map(([k, label]) => (
            <label className="radio" key={k}>
              <input type="radio" name="kind" checked={kind === k} onChange={() => changeKind(k)} />
              <span>{label}</span>
            </label>
          ))}
        </fieldset>

        {kind === "local" && (
          <>
            <label>
              <span>{t("new.folder")}</span>
              {!showCustom && (
                <FolderPicker
                  value={folder}
                  onChange={(path, folderName) => {
                    setFolder(path);
                    if (!name.trim()) setName(folderName);
                  }}
                />
              )}
              {showCustom && (
                <input className="mono" value={customPath} onChange={(e) => setCustomPath(e.target.value)} placeholder="/sources/my-app" />
              )}
              <small className="muted">
                {t("new.folderHint")}{" "}
                <button type="button" className="link" onClick={() => setShowCustom(!showCustom)}>
                  {showCustom ? t("new.folder") : t("new.folderCustom")}
                </button>
              </small>
            </label>
          </>
        )}

        {kind === "upload" && (
          <label>
            <span>{t("new.kind.upload")}</span>
            <div className="discover-row">
              {supportsDirectoryPicker() ? (
                <button type="button" className="button" onClick={chooseFolder} disabled={uploadProgress !== null}>
                  📂 {t("new.uploadPick")}
                </button>
              ) : (
                <input type="file" onChange={(e) => chooseFolderFromInput(e.target.files)} {...({ webkitdirectory: "", directory: "" } as Record<string, string>)} />
              )}
              {uploadProgress && (
                <span className="muted small">
                  {uploadProgress.phase === "reading" && t("new.uploadReading", { files: uploadProgress.files })}
                  {uploadProgress.phase === "zipping" && t("new.uploadZipping", { percent: uploadProgress.percent ?? 0 })}
                  {uploadProgress.phase === "uploading" && t("new.uploadSending", { mb: (uploadProgress.bytes / 1048576).toFixed(1) })}
                </span>
              )}
              {upload && (
                <span className="banner ok small">
                  {t("new.uploadDone", { name: upload.name, files: upload.files, mb: (upload.bytes / 1048576).toFixed(1), skipped: upload.skipped })}
                </span>
              )}
            </div>
            {!supportsDirectoryPicker() && <small className="muted">{t("new.uploadUnsupported")}</small>}
            <small className="muted">{t("new.uploadHint")}</small>
            {uploadError && <ErrorBox message={uploadError} />}
          </label>
        )}

        {kind === "git" && (
          <>
            <label>
              <span>{t("new.url")}</span>
              <input className="mono" value={url} onChange={(e) => setUrl(e.target.value)} placeholder={t("new.urlPlaceholder")} />
            </label>
            <label>
              <span>{t("new.branch")}</span>
              <input className="mono" value={branch} onChange={(e) => setBranch(e.target.value)} placeholder="main" />
            </label>
            {credentialSelect(t("new.credentialHint"))}
          </>
        )}

        {isProvider && (
          <>
            <label>
              <span>{t(`new.locator.${providerKey}` as "new.locator.github")}</span>
              <div className="discover-row">
                <input
                  className="mono"
                  value={locator}
                  onChange={(e) => {
                    setLocator(e.target.value);
                    setDiscovered(null);
                    setSelected(new Set());
                  }}
                  placeholder={kind === "github" ? "my-org/billing-api" : kind === "gitlab" ? "acme/platform/billing-api" : "contoso/Payments"}
                  style={{ flex: 1 }}
                />
                <button type="button" className="button" onClick={discover} disabled={discovering || !locator.trim()}>
                  {discovering ? t("new.discovering") : `🔎 ${t("new.discover")}`}
                </button>
              </div>
              <small className="muted">{t(`new.locatorHint.${providerKey}` as "new.locatorHint.github")}</small>
            </label>
            {credentialSelect(t("new.credentialHintProvider"))}
            <label>
              <span>{t("new.branch")}</span>
              <input className="mono" value={branch} onChange={(e) => setBranch(e.target.value)} placeholder={t("new.defaultBranch")} />
            </label>

            {discovered && discovered.length === 0 && <p className="muted">{t("new.discoveredNone")}</p>}
            {discovered && discovered.length > 0 && (
              <div>
                <p className="muted small discover-row">
                  <span>{t("new.discovered", { count: discovered.length })}</span>
                  <button
                    type="button"
                    className="button small"
                    onClick={() => setSelected(new Set(discovered.filter((r) => !r.archived).map((r) => r.locator)))}
                  >
                    {t("new.selectAll")}
                  </button>
                  <button type="button" className="button small" onClick={() => setSelected(new Set())}>
                    {t("new.clearSelection")}
                  </button>
                </p>
                <table className="repo-table">
                  <thead>
                    <tr>
                      <th />
                      <th>{t("new.repo")}</th>
                      <th>{t("new.defaultBranch")}</th>
                      <th>{t("new.language")}</th>
                      <th>{t("new.lastPush")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {discovered.map((r) => (
                      <tr key={r.locator} className={r.archived ? "archived" : ""}>
                        <td>
                          <input type="checkbox" checked={selected.has(r.locator)} onChange={() => toggle(r.locator)} />
                        </td>
                        <td>
                          <span className="strong">{r.name}</span>
                          {r.isPrivate && <span className="tag">{t("new.private")}</span>}
                          {r.archived && <span className="tag">{t("new.archived")}</span>}
                          <div className="mono small muted">{r.locator}</div>
                        </td>
                        <td className="mono small">{r.defaultBranch ?? "—"}</td>
                        <td className="small">{r.language ?? "—"}</td>
                        <td className="small">{r.lastPushUtc ? formatDate(r.lastPushUtc) : "—"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}

        <label>
          <span>{t("new.exclude")}</span>
          <textarea className="mono" rows={3} value={excludes} onChange={(e) => setExcludes(e.target.value)} placeholder={"legacy-copy/\n**/*.generated.cs"} />
          <small className="muted">{t("new.excludeHint")}</small>
        </label>

        {error && <ErrorBox message={error} />}

        <div className="actions">
          <button type="submit" className="button primary" disabled={!canSubmit}>
            {submitting ? t("new.submitting") : isProvider ? t("new.createMany", { count: selected.size }) : t("new.submit")}
          </button>
        </div>
      </form>
    </>
  );
}
