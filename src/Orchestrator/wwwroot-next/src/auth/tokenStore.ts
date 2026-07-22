// Access-token store. Two rules that are security-load-bearing (DESIGN §10):
//  1. The REFRESH token is an httpOnly cookie — JS must NEVER read or write it.
//     It rides automatically via credentials:'include' (see api/client.ts).
//  2. The ACCESS token lives in memory (source of truth) with a localStorage
//     mirror only so a page reload can rehydrate before the first refresh.
//
// Keep this tiny and synchronous. No token PARSING here beyond expiry hints.

const STORAGE_KEY = "dc-access-token";

let inMemory: string | null = null;

export const tokenStore = {
  get(): string | null {
    if (inMemory) return inMemory;
    try {
      inMemory = localStorage.getItem(STORAGE_KEY);
    } catch {
      inMemory = null;
    }
    return inMemory;
  },

  set(token: string | null): void {
    inMemory = token;
    try {
      if (token) localStorage.setItem(STORAGE_KEY, token);
      else localStorage.removeItem(STORAGE_KEY);
    } catch {
      /* private mode / disabled storage — memory still works for this session */
    }
  },

  clear(): void {
    this.set(null);
  },
};
