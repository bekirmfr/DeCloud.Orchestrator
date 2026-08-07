import { useCallback, useEffect, useState } from "react";

/**
 * Theme control that matches the pre-paint script in index.html exactly.
 *
 * Contract (do not diverge from index.html):
 *   - localStorage["dc-theme"] holds "light" | "dark", or is ABSENT for "system".
 *   - On load, the inline pre-paint script resolves absent/"system" to a concrete
 *     "light"/"dark" via prefers-color-scheme and sets <html data-theme>.
 *
 * So here:
 *   - "light"/"dark": persist the key + pin data-theme.
 *   - "system": remove the key + resolve to the OS preference now (what the
 *     pre-paint would do on the next load), and track OS changes live.
 */
export type ThemeChoice = "light" | "dark" | "system";

const STORAGE_KEY = "dc-theme";

function systemPrefersDark(): boolean {
  return typeof window !== "undefined"
    && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function readChoice(): ThemeChoice {
  const stored = typeof localStorage !== "undefined"
    ? localStorage.getItem(STORAGE_KEY)
    : null;
  return stored === "light" || stored === "dark" ? stored : "system";
}

/** Set <html data-theme> to a concrete value. Never leaves it unset, matching pre-paint. */
function applyResolved(choice: ThemeChoice): void {
  const resolved = choice === "system"
    ? (systemPrefersDark() ? "dark" : "light")
    : choice;
  document.documentElement.setAttribute("data-theme", resolved);
}

export function useTheme() {
  const [theme, setThemeState] = useState<ThemeChoice>(readChoice);

  const setTheme = useCallback((choice: ThemeChoice) => {
    if (choice === "system") {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      localStorage.setItem(STORAGE_KEY, choice);
    }
    applyResolved(choice);
    setThemeState(choice);
  }, []);

  // While on "system", follow live OS theme changes (the CSS media query is a
  // backup, but data-theme is always concrete, so re-resolve it on change).
  useEffect(() => {
    if (theme !== "system") return;
    const mql = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => applyResolved("system");
    mql.addEventListener("change", onChange);
    return () => mql.removeEventListener("change", onChange);
  }, [theme]);

  return { theme, setTheme };
}
