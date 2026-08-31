import { useState } from "react";
import { useI18n } from "../i18n";

/** Thumbs up / down on something the model wrote; a thumbs-down opens a one-line optional comment. Last vote wins. */
export function FeedbackBar({ rating, onRate, compact = false }: { rating: number | null | undefined; onRate: (rating: number, comment: string | null) => Promise<void>; compact?: boolean }) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [asking, setAsking] = useState(false);
  const [comment, setComment] = useState("");
  const [done, setDone] = useState<string | null>(null);

  async function send(value: number, text: string | null) {
    setBusy(true);
    try {
      await onRate(value, text);
      setAsking(false);
      setComment("");
      setDone(value === 0 ? null : t("feedback.thanks"));
      setTimeout(() => setDone(null), 2500);
    } finally {
      setBusy(false);
    }
  }

  return (
    <span className={`feedback${compact ? " compact" : ""}`}>
      {!compact && <span className="muted small">{t("feedback.ask")}</span>}
      <button type="button" className={`icon-btn${rating === 1 ? " on" : ""}`} title={t("feedback.up")} aria-pressed={rating === 1} disabled={busy} onClick={() => send(rating === 1 ? 0 : 1, null)}>
        👍
      </button>
      <button type="button" className={`icon-btn${rating === -1 ? " on" : ""}`} title={t("feedback.down")} aria-pressed={rating === -1} disabled={busy} onClick={() => (rating === -1 ? send(0, null) : setAsking((a) => !a))}>
        👎
      </button>
      {asking && (
        <form
          className="feedback-form"
          onSubmit={(e) => {
            e.preventDefault();
            void send(-1, comment.trim() || null);
          }}
        >
          <input value={comment} maxLength={500} placeholder={t("feedback.why")} onChange={(e) => setComment(e.target.value)} autoFocus />
          <button type="submit" className="button small" disabled={busy}>{t("feedback.send")}</button>
        </form>
      )}
      {done && <span className="muted small">{done}</span>}
    </span>
  );
}
