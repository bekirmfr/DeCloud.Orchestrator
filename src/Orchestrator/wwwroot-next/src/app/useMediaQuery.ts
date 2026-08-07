import { useEffect, useState } from "react";

// Because the app styles inline (React style objects can't carry @media rules),
// responsive layout is branched in JS: a component reads a breakpoint and picks
// the layout. Kept tiny and SSR-safe.
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(
    () => typeof window !== "undefined" && window.matchMedia(query).matches,
  );

  useEffect(() => {
    const mql = window.matchMedia(query);
    const onChange = () => setMatches(mql.matches);
    onChange();
    mql.addEventListener("change", onChange);
    return () => mql.removeEventListener("change", onChange);
  }, [query]);

  return matches;
}

/** True on phone/small-tablet widths — the shell collapses its nav into a drawer here. */
export const MOBILE_QUERY = "(max-width: 880px)";
