import { describe, expect, it } from "vitest";
import { dictionaries } from "../i18n";

/** Every key exists in both languages with the same placeholders — a missing translation shows up here, not in the UI. */
describe("i18n dictionaries", () => {
  const en = dictionaries.en as Record<string, string>;
  const pt = dictionaries["pt-BR"] as Record<string, string>;

  it("pt-BR covers every English key", () => {
    const missing = Object.keys(en).filter((k) => !(k in pt));
    expect(missing).toEqual([]);
  });

  it("English covers every pt-BR key", () => {
    const extra = Object.keys(pt).filter((k) => !(k in en));
    expect(extra).toEqual([]);
  });

  it("placeholders match between languages", () => {
    const vars = (s: string) => (s.match(/\{[a-zA-Z0-9_]+\}/g) ?? []).sort();
    const mismatched = Object.keys(en).filter((k) => k in pt && vars(en[k]).join(",") !== vars(pt[k]).join(","));
    expect(mismatched).toEqual([]);
  });

  it("has no empty strings", () => {
    const empty = [...Object.entries(en), ...Object.entries(pt)].filter(([, v]) => v.trim().length === 0).map(([k]) => k);
    expect(empty).toEqual([]);
  });
});
