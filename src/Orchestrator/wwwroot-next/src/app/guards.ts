import type { AuthUser } from "../auth/types";

// Route guards are UX ONLY — the server enforces [Authorize(Roles="Admin")]
// (capital-A). These just decide what to show/redirect. Pure + tested.

/** Admin-area visibility. Consolidates the legacy scattered
 *  `if (!tokenHasAdminRole) showPage('dashboard')` into one predicate. */
export function canAccessAdmin(user: AuthUser | null | undefined): boolean {
  return user?.roles?.includes("Admin") === true;
}
