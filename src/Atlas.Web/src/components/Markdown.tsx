import type { ReactNode } from "react";

/**
 * Renders the small Markdown subset the AI narratives use — `#`..`###` headings,
 * `-`/`*` bullets, `1.` numbered items, **bold**, `code`, paragraphs — as React
 * elements. Text only: no raw HTML, no links, so model output can never inject markup.
 */
export function Markdown({ text, headingOffset = 1 }: { text: string; headingOffset?: number }) {
  const blocks: ReactNode[] = [];
  let paragraph: string[] = [];
  let list: { tag: "ul" | "ol"; items: string[] } | null = null;

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      blocks.push(<p key={blocks.length}>{inline(paragraph.join(" "))}</p>);
      paragraph = [];
    }
  };
  const flushList = () => {
    if (list) {
      const items = list.items.map((item, i) => <li key={i}>{inline(item)}</li>);
      blocks.push(list.tag === "ul" ? <ul key={blocks.length}>{items}</ul> : <ol key={blocks.length}>{items}</ol>);
      list = null;
    }
  };

  const lines = text.replace(/\r\n/g, "\n").split("\n");
  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const line = raw.trimEnd();
    const fence = /^```\s*([a-zA-Z0-9_-]*)\s*$/.exec(line);
    if (fence) {
      flushParagraph();
      flushList();
      const lang = fence[1].toLowerCase();
      const body: string[] = [];
      i++;
      while (i < lines.length && !/^```\s*$/.test(lines[i].trimEnd())) body.push(lines[i++]);
      blocks.push(
        <pre key={blocks.length} className={`code${lang ? ` lang-${lang}` : ""}`}>
          {lang === "diff" || lang === "patch"
            ? body.map((l, n) => (
                <span key={n} className={l.startsWith("+") && !l.startsWith("+++") ? "diff-add" : l.startsWith("-") && !l.startsWith("---") ? "diff-del" : l.startsWith("@@") ? "diff-hunk" : l.startsWith("+++") || l.startsWith("---") ? "diff-file" : ""}>
                  {l}
                  {"\n"}
                </span>
              ))
            : body.join("\n")}
        </pre>,
      );
      continue;
    }
    if (line.length === 0) {
      flushParagraph();
      flushList();
      continue;
    }
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      const level = Math.min(6, Math.max(1, heading[1].length + headingOffset));
      const content = inline(heading[2].replace(/[#\s]+$/, ""));
      const key = blocks.length;
      blocks.push(
        level <= 2 ? <h2 key={key}>{content}</h2> : level === 3 ? <h3 key={key}>{content}</h3> : level === 4 ? <h4 key={key}>{content}</h4> : <h5 key={key}>{content}</h5>,
      );
      continue;
    }
    const bullet = /^\s*[-*•]\s+(.*)$/.exec(line);
    const numbered = bullet ? null : /^\s*\d{1,3}[.)]\s+(.*)$/.exec(line);
    if (bullet || numbered) {
      flushParagraph();
      const tag = bullet ? "ul" : "ol";
      if (!list || list.tag !== tag) {
        flushList();
        list = { tag, items: [] };
      }
      list.items.push((bullet ?? numbered)![1]);
      continue;
    }
    if (list && /^\s{2,}/.test(raw)) {
      list.items[list.items.length - 1] += " " + line.trim();
      continue;
    }
    flushList();
    paragraph.push(line.trim());
  }
  flushParagraph();
  flushList();
  return <>{blocks}</>;
}

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /\*\*(.+?)\*\*|`([^`]+)`/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    out.push(m[1] !== undefined ? <strong key={m.index}>{m[1]}</strong> : <code key={m.index}>{m[2]}</code>);
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}
