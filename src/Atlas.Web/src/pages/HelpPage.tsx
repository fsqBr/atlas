import { useEffect, useMemo, useState, type MouseEvent } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router-dom";
import { api } from "../api";
import { Markdown } from "../components/Markdown";
import { PageHeader } from "../components/ui";
import { HELP_GROUPS, HELP_PLACEHOLDERS, HELP_SECTIONS, type HelpGroup, type HelpSection } from "../help/content";
import { useI18n, type Lang } from "../i18n";

type Facts = Record<(typeof HELP_PLACEHOLDERS)[number], string>;

const PENDING = "…";

function emptyFacts(): Facts {
  return Object.fromEntries(HELP_PLACEHOLDERS.map((k) => [k, PENDING])) as Facts;
}

/** Replaces the manual's `{placeholders}` with live facts; anything else in braces (route templates, code) is left alone. */
export function resolvePlaceholders(body: string, facts: Partial<Facts>): string {
  return body.replace(/\{([a-zA-Z]+)\}/g, (whole, key: string) =>
    (HELP_PLACEHOLDERS as readonly string[]).includes(key) ? (facts[key as keyof Facts] ?? PENDING) : whole,
  );
}

/** Plain text of a section for search: Markdown markers stripped, both languages so a PT reader finds English rule ids too. */
function searchable(section: HelpSection, lang: Lang): string {
  return `${section.title[lang]} ${section.body[lang]} ${section.title.en} ${section.id}`.replace(/[`*#|\[\]()>-]/g, " ").toLowerCase();
}

/** The in-app manual: searchable, grouped, deep-linkable (`#section`, `?q=`), with live facts from this instance. */
export function HelpPage() {
  const { t, lang } = useI18n();
  const location = useLocation();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const query = params.get("q") ?? "";
  const [facts, setFacts] = useState<Facts>(emptyFacts);

  useEffect(() => {
    let cancelled = false;
    const dateFmt = new Intl.DateTimeFormat(lang, { dateStyle: "medium", timeStyle: "short" });
    const yes = t("help.yes");
    const no = t("help.no");
    Promise.allSettled([api.getVersion(), api.authConfig(), api.me(), api.costSchedule(), api.getUsagePrices(), api.costProviders(), api.catalogProviders()]).then(
      ([version, auth, me, schedule, prices, costProviders, catalog]) => {
        if (cancelled) return;
        const next = emptyFacts();
        next.origin = window.location.origin;
        next.version = version.status === "fulfilled" ? version.value.version : t("help.unavailable");
        next.auth = auth.status === "fulfilled" ? (auth.value.enabled ? t("help.authOn") : t("help.authOff")) : t("help.unavailable");
        if (me.status === "fulfilled") {
          next.roles = me.value.roles.length > 0 ? me.value.roles.join(", ") : t("help.rolesNone");
          next.tenant = me.value.isDefaultTenant || !me.value.tenantName ? t("help.tenantDefault") : me.value.tenantName;
        } else {
          next.roles = t("help.unavailable");
          next.tenant = t("help.unavailable");
        }
        if (schedule.status === "fulfilled") {
          next.syncEnabled = schedule.value.enabled ? yes : no;
          next.syncHour = String(schedule.value.hourUtc).padStart(2, "0");
          next.syncNext = schedule.value.nextRunUtc ? dateFmt.format(new Date(schedule.value.nextRunUtc)) : t("help.notScheduled");
        } else {
          next.syncEnabled = t("help.unavailable");
          next.syncHour = "06";
          next.syncNext = t("help.unavailable");
        }
        if (prices.status === "fulfilled") {
          next.priceCatalog = prices.value.version;
          next.unpriced = String(prices.value.unpriced.length);
        } else {
          next.priceCatalog = t("help.unavailable");
          next.unpriced = t("help.unavailable");
        }
        next.costProviders = costProviders.status === "fulfilled" ? costProviders.value.join(", ") : t("help.unavailable");
        next.providers = catalog.status === "fulfilled" ? String(catalog.value.length) : t("help.unavailable");
        setFacts(next);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [lang, t]);

  const needle = query.trim().toLowerCase();
  const visible = useMemo(
    () => (needle ? HELP_SECTIONS.filter((s) => searchable(s, lang).includes(needle)) : HELP_SECTIONS),
    [needle, lang],
  );
  const activeId = location.hash.replace(/^#/, "");

  // Scroll to the section named in the hash once it exists (after the first render and whenever the hash changes).
  useEffect(() => {
    if (!activeId) return;
    const el = document.getElementById(`help-${activeId}`);
    if (el) el.scrollIntoView({ block: "start" });
  }, [activeId, visible]);

  function onBodyClick(e: MouseEvent<HTMLElement>) {
    const anchor = (e.target as HTMLElement).closest("a");
    if (!anchor) return;
    const href = anchor.getAttribute("href") ?? "";
    if (!href.startsWith("/")) return;
    e.preventDefault();
    const [path, hash] = href.split("#");
    if (path === "/help" && hash) {
      setParams((p) => {
        p.delete("q");
        return p;
      });
      navigate({ pathname: "/help", hash: `#${hash}` });
    } else {
      navigate(href);
    }
  }

  const grouped = HELP_GROUPS.map((g) => ({ group: g, sections: visible.filter((s) => s.group === g) })).filter((g) => g.sections.length > 0);

  return (
    <div>
      <PageHeader title={t("help.title")} subtitle={t("help.intro")} />
      <div className="help">
        <aside className="help-toc" aria-label={t("help.toc")}>
          <input
            type="search"
            value={query}
            placeholder={t("help.search")}
            aria-label={t("help.search")}
            onChange={(e) =>
              setParams((p) => {
                if (e.target.value) p.set("q", e.target.value);
                else p.delete("q");
                return p;
              })
            }
          />
          {needle && (
            <p className="muted small" data-testid="help-count">
              {t("help.results", { count: visible.length })}
            </p>
          )}
          <nav>
            {grouped.map(({ group, sections }) => (
              <div key={group} className="help-group">
                <div className="side-group">{t(`help.group.${group}` as `help.group.${HelpGroup}`)}</div>
                {sections.map((s) => (
                  <a key={s.id} href={`/help#${s.id}`} className={s.id === activeId ? "active" : undefined} onClick={onBodyClick}>
                    {s.title[lang]}
                  </a>
                ))}
              </div>
            ))}
          </nav>
        </aside>
        <div className="help-body" onClick={onBodyClick}>
          {visible.length === 0 && <p className="muted">{t("help.noResults")}</p>}
          {grouped.map(({ group, sections }) => (
            <div key={group}>
              <p className="eyebrow">{t(`help.group.${group}` as `help.group.${HelpGroup}`)}</p>
              {sections.map((s) => (
                <section key={s.id} id={`help-${s.id}`} className={`card help-section${s.id === activeId ? " active" : ""}`}>
                  <h2>{s.title[lang]}</h2>
                  <Markdown text={resolvePlaceholders(s.body[lang], facts)} headingOffset={0} links />
                </section>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
