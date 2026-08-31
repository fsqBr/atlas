import { useEffect, useState } from "react";

/** Light / dark / follow the system. Stored per browser; applied as data-theme on <html> so CSS tokens switch. */
export type Theme = "light" | "dark" | "system";

const KEY = "atlas.theme";
const EVENT = "atlas-theme";

export function readTheme(): Theme {
  try {
    const v = localStorage.getItem(KEY);
    if (v === "light" || v === "dark" || v === "system") return v;
  } catch {
    /* storage unavailable */
  }
  return "system";
}

export function applyTheme(theme: Theme) {
  const root = document.documentElement;
  if (theme === "system") root.removeAttribute("data-theme");
  else root.setAttribute("data-theme", theme);
  try {
    localStorage.setItem(KEY, theme);
  } catch {
    /* ignore */
  }
  window.dispatchEvent(new CustomEvent(EVENT));
}

/** Resolved appearance right now (system preference taken into account). */
export function isDark(): boolean {
  const explicit = document.documentElement.getAttribute("data-theme");
  if (explicit) return explicit === "dark";
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

export function useTheme(): [Theme, (t: Theme) => void] {
  const [theme, setTheme] = useState<Theme>(readTheme);
  useEffect(() => {
    const on = () => setTheme(readTheme());
    window.addEventListener(EVENT, on);
    return () => window.removeEventListener(EVENT, on);
  }, []);
  return [theme, applyTheme];
}

/** Bumps whenever the theme (explicit or system) changes — charts re-read their CSS token colors on it. */
export function useThemeVersion(): number {
  const [v, setV] = useState(0);
  useEffect(() => {
    const bump = () => setV((x) => x + 1);
    window.addEventListener(EVENT, bump);
    const media = window.matchMedia?.("(prefers-color-scheme: dark)");
    media?.addEventListener?.("change", bump);
    return () => {
      window.removeEventListener(EVENT, bump);
      media?.removeEventListener?.("change", bump);
    };
  }, []);
  return v;
}

/** Apply the stored theme before the first paint; `?theme=light|dark|system` in the URL overrides and is remembered (demos, screenshots). */
export function initTheme() {
  const fromUrl = new URLSearchParams(window.location.search).get("theme");
  if (fromUrl === "light" || fromUrl === "dark" || fromUrl === "system") {
    applyTheme(fromUrl);
    return;
  }
  const theme = readTheme();
  if (theme !== "system") document.documentElement.setAttribute("data-theme", theme);
}
