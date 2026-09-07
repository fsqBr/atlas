import type { ReactNode } from "react";

/**
 * Renders the small Markdown subset the AI narratives and the in-app manual use — `#`..`###` headings,
 * `-`/`*` bullets, `1.` numbered items, pipe tables, fenced code, **bold**, *italic*, `code`, paragraphs — as
 * React elements. Text only: no raw HTML, so model output can never inject markup. `[text](url)` links are
 * rendered only when `links` is set (the manual); AI text keeps them as plain text.
 */
export function Markdown({ text, headingOffset = 1, links = false }: { text: string; headingOffset?: number; links?: boolean }) {
  const blocks: ReactNode[] = [];
  let paragraph: string[] = [];
  let list: { tag: "ul" | "ol"; items: string[] } | null = null;
  const render = (s: string) => inline(s, links);

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      blocks.push(<p key={blocks.length}>{render(paragraph.join(" "))}</p>);
      paragraph = [];
    }
  };
  const flushList = () => {
    if (list) {
      const items = list.items.map((item, i) => <li key={i}>{render(item)}</li>);
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
    // Pipe table: a header row followed by a |---| separator row.
    if (line.startsWith("|") && i + 1 < lines.length && /^\|?\s*:?-{2,}/.test(lines[i + 1].trim())) {
      flushParagraph();
      flushList();
      const header = splitCells(line);
      const rows: string[][] = [];
      i += 2;
      while (i < lines.length && lines[i].trim().startsWith("|")) rows.push(splitCells(lines[i++].trimEnd()));
      i--;
      blocks.push(
        <div key={blocks.length} className="table-wrap">
          <table>
            <thead>
              <tr>
                {header.map((h, n) => (
                  <th key={n}>{render(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, n) => (
                <tr key={n}>
                  {header.map((_, c) => (
                    <td key={c}>{render(r[c] ?? "")}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>,
      );
      continue;
    }
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      const level = Math.min(6, Math.max(1, heading[1].length + headingOffset));
      const content = render(heading[2].replace(/[#\s]+$/, ""));
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

/** Splits a table row on `|`, ignoring pipes inside `code` spans so `csv|json` stays one cell. */
function splitCells(row: string): string[] {
  const cells: string[] = [];
  let current = "";
  let inCode = false;
  for (const ch of row.trim()) {
    if (ch === "`") inCode = !inCode;
    if (ch === "|" && !inCode) {
      cells.push(current.trim());
      current = "";
    } else {
      current += ch;
    }
  }
  cells.push(current.trim());
  if (cells.length > 0 && cells[0] === "") cells.shift();
  if (cells.length > 0 && cells[cells.length - 1] === "") cells.pop();
  return cells;
}

function inline(text: string, links: boolean): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /`([^`]+)`|\*\*(.+?)\*\*|\*([^*\s][^*]*?)\*|\[([^\]]+)\]\(([^)\s]+)\)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    if (m[1] !== undefined) out.push(<code key={m.index}>{m[1]}</code>);
    else if (m[2] !== undefined) out.push(<strong key={m.index}>{inline(m[2], links)}</strong>);
    else if (m[3] !== undefined) out.push(<em key={m.index}>{m[3]}</em>);
    else if (links && (m[5].startsWith("/") || m[5].startsWith("https://"))) {
      out.push(
        <a key={m.index} href={m[5]} target={m[5].startsWith("https://") ? "_blank" : undefined} rel={m[5].startsWith("https://") ? "noreferrer" : undefined}>
          {m[4]}
        </a>,
      );
    } else out.push(m[0]);
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}
