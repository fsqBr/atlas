import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, type AiSettings, type AiTestResult, type AiFeedbackSummary } from "../api";
import { ErrorBox, Spinner } from "../components";
import { Link } from "react-router-dom";
import { useI18n } from "../i18n";

/**
 * Settings → AI: pick a provider, paste the key (stored encrypted, never shown again),
 * test the connection, and switch AI analysis on. Everything the model will ever see is
 * described on this page so the decision to enable it is an informed one.
 */
export function AiSettingsPage() {
  const { t, formatDate } = useI18n();
  const [settings, setSettings] = useState<AiSettings | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [provider, setProvider] = useState("Anthropic");
  const [model, setModel] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [enabled, setEnabled] = useState(false);
  const [maxSnippets, setMaxSnippets] = useState("40");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [testing, setTesting] = useState(false);
  const [test, setTest] = useState<AiTestResult | null>(null);

  const load = useCallback(() => {
    api
      .getAiSettings()
      .then((s) => {
        setSettings(s);
        setProvider(s.provider);
        setModel(s.model);
        setBaseUrl(s.baseUrl ?? "");
        setEnabled(s.enabled);
        setMaxSnippets(String(s.maxSnippetsPerAnalysis));
        setError(null);
      })
      .catch(() => setError(t("error.load")));
  }, [t]);

  useEffect(() => {
    load();
  }, [load]);

  if (error) return <ErrorBox message={error} />;
  if (!settings) return <Spinner />;

  const info = settings.providers.find((p) => p.id === provider) ?? settings.providers[0];
  const keyMissing = info.requiresKey && !settings.hasKey && apiKey.trim().length === 0;
  const providerChanged = provider !== settings.provider;
  const needsKeyNow = info.requiresKey && (keyMissing || providerChanged) && apiKey.trim().length === 0;

  function pickProvider(id: string) {
    setProvider(id);
    const p = settings!.providers.find((x) => x.id === id);
    if (p) {
      setModel(p.defaultModel);
      setBaseUrl(p.defaultBaseUrl ?? "");
    }
    setTest(null);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setFormError(null);
    setSaved(false);
    setTest(null);
    try {
      const s = await api.saveAiSettings({
        provider,
        model: model.trim() || null,
        baseUrl: baseUrl.trim() || null,
        apiKey: apiKey.trim() || null,
        enabled: enabled && !needsKeyNow,
        maxSnippetsPerAnalysis: Number(maxSnippets) || null,
      });
      setSettings(s);
      setApiKey("");
      setEnabled(s.enabled);
      setSaved(true);
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function runTest() {
    setTesting(true);
    setTest(null);
    setFormError(null);
    try {
      setTest(await api.testAi());
      load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    } finally {
      setTesting(false);
    }
  }

  async function useLocalOllama() {
    const local = settings?.localOllama;
    if (!local?.url) return;
    setSaving(true);
    setFormError(null);
    setSaved(false);
    setTest(null);
    try {
      const modelName = local.models.includes(local.defaultModel) ? local.defaultModel : local.models[0] ?? local.defaultModel;
      const s = await api.saveAiSettings({ provider: "Ollama", model: modelName, baseUrl: `${local.url}/v1`, apiKey: null, enabled: true, maxSnippetsPerAnalysis: Number(maxSnippets) || null });
      setSettings(s);
      setProvider(s.provider);
      setModel(s.model);
      setBaseUrl(s.baseUrl ?? "");
      setEnabled(s.enabled);
      setSaved(true);
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function clearKey() {
    if (!window.confirm(t("ai.confirmClearKey"))) return;
    try {
      setSettings(await api.clearAiKey());
      setEnabled(false);
    } catch (err) {
      setFormError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>{t("ai.title")}</h1>
        <span className={`chip ${settings.usable ? "ok" : ""}`}>{settings.usable ? t("ai.statusOn") : t("ai.statusOff")}</span>
      </div>
      <p className="muted">{t("ai.intro")}</p>

      {!settings.secretStoreConfigured && <ErrorBox message={t("ai.noMasterKey")} />}

      <div className="card banner-info">
        <strong>{t("ai.privacyTitle")}</strong>
        <ul className="small">
          <li>{t("ai.privacy1")}</li>
          <li>{t("ai.privacy2")}</li>
          <li>{t("ai.privacy3")}</li>
          <li>{t("ai.privacy4")}</li>
        </ul>
      </div>

      {settings.localOllama?.url && (
        <div className="card">
          <strong>{t("ai.local.title")}</strong>
          <p className="small muted">
            {settings.localOllama.available
              ? settings.localOllama.models.length > 0
                ? t("ai.local.available", { url: settings.localOllama.url, models: settings.localOllama.models.join(", ") })
                : t("ai.local.pulling", { model: settings.localOllama.defaultModel })
              : t("ai.local.unavailable", { model: settings.localOllama.defaultModel })}
          </p>
          <div className="actions">
            <button type="button" className="button" onClick={useLocalOllama} disabled={saving || !settings.localOllama.available || settings.localOllama.models.length === 0}>
              🖥 {t("ai.local.use")}
            </button>
            <span className="muted small">{t("ai.local.note")}</span>
          </div>
        </div>
      )}

      <form className="card form" onSubmit={save}>
        <label>
          <span>{t("ai.provider")}</span>
          <select value={provider} onChange={(e) => pickProvider(e.target.value)}>
            {settings.providers.map((p) => (
              <option key={p.id} value={p.id}>
                {t(`ai.provider.${p.id}` as "ai.provider.Anthropic")}
              </option>
            ))}
          </select>
          <small className="muted">{t(`ai.providerHint.${provider}` as "ai.providerHint.Anthropic")}</small>
        </label>

        <label>
          <span>{t("ai.model")}</span>
          <input className="mono" value={model} onChange={(e) => setModel(e.target.value)} placeholder={info.defaultModel} />
          <small className="muted">{provider === "AzureOpenAI" ? t("ai.modelHintAzure") : t("ai.modelHint", { model: info.defaultModel })}</small>
        </label>

        <label>
          <span>{t("ai.baseUrl")}</span>
          <input className="mono" value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder={info.defaultBaseUrl ?? "https://<resource>.openai.azure.com"} />
          <small className="muted">{t("ai.baseUrlHint")}</small>
        </label>

        {info.requiresKey && (
          <label>
            <span>{t("ai.apiKey")}</span>
            <input
              className="mono"
              type="password"
              autoComplete="off"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder={settings.hasKey && !providerChanged ? t("ai.keyStored") : t("ai.keyPlaceholder")}
            />
            <small className="muted">
              {settings.hasKey && !providerChanged ? t("ai.keyStoredHint") : t("ai.keyHint")}{" "}
              {settings.hasKey && (
                <button type="button" className="link" onClick={clearKey}>
                  {t("ai.clearKey")}
                </button>
              )}
            </small>
          </label>
        )}

        <label>
          <span>{t("ai.maxSnippets")}</span>
          <input type="number" min={1} max={500} value={maxSnippets} onChange={(e) => setMaxSnippets(e.target.value)} style={{ maxWidth: "8rem" }} />
          <small className="muted">{t("ai.maxSnippetsHint")}</small>
        </label>

        <label className="check">
          <input type="checkbox" checked={enabled} disabled={needsKeyNow} onChange={(e) => setEnabled(e.target.checked)} />
          <span>{t("ai.enable")}</span>
          {needsKeyNow && <small className="muted"> — {t("ai.enableNeedsKey")}</small>}
        </label>

        {formError && <ErrorBox message={formError} />}
        {saved && <p className="banner ok">{t("ai.saved")}</p>}

        <div className="actions">
          <button type="submit" className="button primary" disabled={saving || !settings.secretStoreConfigured}>
            {saving ? t("ai.saving") : t("ai.save")}
          </button>
          <button type="button" className="button" onClick={runTest} disabled={testing || !settings.configured || (info.requiresKey && !settings.hasKey)}>
            {testing ? t("ai.testing") : t("ai.test")}
          </button>
          {test && <span className={`banner small ${test.succeeded ? "ok" : "error"}`}>{test.message}</span>}
        </div>
      </form>

      <QualityCard />

      {settings.configured && (
        <p className="muted small">
          {t("ai.updated")} {settings.updatedAtUtc ? formatDate(settings.updatedAtUtc) : "—"}
          {settings.lastTestedAtUtc && (
            <>
              {" · "}
              {t("ai.lastTest")} {formatDate(settings.lastTestedAtUtc)}: {settings.lastTestSucceeded ? "✓" : "✗"} {settings.lastTestMessage}
            </>
          )}
        </p>
      )}
    </>
  );
}

/** What people thought of the AI answers: thumbs per kind and per model, latest comments. */
function QualityCard() {
  const { t, formatDate } = useI18n();
  const [data, setData] = useState<AiFeedbackSummary | null>(null);
  useEffect(() => {
    api.aiFeedback().then(setData).catch(() => setData(null));
  }, []);
  if (!data) return null;
  const total = data.up + data.down;
  const kindLabel = (k: string) => t(`feedback.kind.${k}` as "feedback.kind.finding-explanation");
  return (
    <div className="card">
      <h2>{t("feedback.quality")}</h2>
      <p className="muted small">{t("feedback.qualityHint")}</p>
      {total === 0 ? (
        <p className="muted">{t("feedback.none")}</p>
      ) : (
        <>
          <div className="kpis">
            <div className="tile tone-ok"><span className="tile-v">👍 {data.up}</span><span className="tile-l">{t("feedback.up")}</span></div>
            <div className="tile tone-high"><span className="tile-v">👎 {data.down}</span><span className="tile-l">{t("feedback.down")}</span></div>
            <div className="tile tone-accent"><span className="tile-v">{Math.round((data.up / total) * 100)}%</span><span className="tile-l">{t("feedback.helpful")}</span></div>
          </div>
          <div className="grid-2">
            <div>
              <h3>{t("feedback.byKind")}</h3>
              <table>
                <tbody>
                  {data.byKind.map((b) => (
                    <tr key={b.key}>
                      <td>{kindLabel(b.key)}</td>
                      <td className="num">👍 {b.up}</td>
                      <td className="num">👎 {b.down}</td>
                      <td className="num">{b.helpfulShare === null ? "—" : `${Math.round(b.helpfulShare * 100)}%`}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div>
              <h3>{t("feedback.byModel")}</h3>
              <table>
                <tbody>
                  {data.byModel.map((b) => (
                    <tr key={b.key}>
                      <td className="mono">{b.key}</td>
                      <td className="num">👍 {b.up}</td>
                      <td className="num">👎 {b.down}</td>
                      <td className="num">{b.helpfulShare === null ? "—" : `${Math.round(b.helpfulShare * 100)}%`}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
          {data.recent.some((e) => e.comment) && (
            <>
              <h3>{t("feedback.comments")}</h3>
              <ul className="attention">
                {data.recent.filter((e) => e.comment).map((e, i) => (
                  <li key={i}>
                    <span>{e.rating > 0 ? "👍" : "👎"}</span>
                    <div>
                      <div>{e.comment}</div>
                      <div className="why">
                        {kindLabel(e.kind)} · {e.title} · <span className="mono">{e.model}</span> · {e.ratedBy ?? "—"} · {formatDate(e.ratedAtUtc)}
                      </div>
                    </div>
                    <Link to={`/assessments/${e.assessmentId}`} className="button small">{t("list.open")}</Link>
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
    </div>
  );
}
