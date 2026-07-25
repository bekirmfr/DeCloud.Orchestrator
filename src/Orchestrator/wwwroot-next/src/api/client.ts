import { definitive, uncertain, type AppError } from "./errors";

/**
 * The uniform response envelope the backend returns everywhere
 * (MarketplaceController was the last raw outlier — normalized).
 *
 * THIS is the contract the app compiles against — a hand-maintained mirror of
 * the backend's ApiResponse<T>, so it can drift; that class is authoritative.
 * `src/api/schema.d.ts` is generated on demand by `npm run gen:api` (against a
 * RUNNING backend on :5050) and is gitignored — nothing imports it. Use it to
 * inspect the live contract when drift is suspected. Adopting it as the source
 * of truth is a deliberate Phase 5 decision, not a pending step.
 *
 * ENUM CAVEAT — most enums are string literals (global JsonStringEnumConverter,
 * and VmRole/VmCategory/SubdomainTier/ServiceStatus carry the per-enum attribute
 * that makes it apply). THREE DO NOT: VmStatus, VmPowerState and VmAction.
 *   - READING: status and powerState arrive as raw INTEGERS over REST
 *     (3 = Running). Never compare to strings.
 *   - WRITING: POST /api/vms/{id}/action expects the ORDINAL. {"action":"Stop"}
 *     is rejected with a 400 — this shipped broken and invisible once already.
 *   - SignalR payloads are hand-built with .ToString() and DO send names, so the
 *     same field can arrive as 3 over REST and "Running" over the hub.
 * Route all three through ../features/vms/vmStatus (normalizeStatus,
 * normalizePowerState, vmActionOrdinal) — never a fresh switch or comparison.
 */
export interface ApiResponse<T> {
  success: boolean;
  data: T | null;
  error?: { code?: string; message?: string } | null;
}

/** Injected so the client stays decoupled from the token store + refresh machine. */
export interface ApiDeps {
  /** Current access token (from tokenStore), or null if none. */
  getToken(): string | null;
  /**
   * Attempt a refresh. Tri-state (DESIGN §4):
   *   true  → refreshed, a new token is now in the store, retry the request
   *   false → definitively expired, give up (caller → NEEDS_AUTH)
   *   null  → could NOT verify (blip); do NOT retry, surface as `uncertain`
   */
  refresh(): Promise<boolean | null>;
}

export interface ApiOptions extends RequestInit {
  /** Set false to skip the one-shot refresh-retry (e.g. the refresh call itself). */
  retryOn401?: boolean;
}

/**
 * The single API boundary. Every feature calls this, never fetch() directly.
 * Responsibilities (keep them here, out of feature code):
 *  - attach bearer token + credentials:'include' (httpOnly refresh cookie)
 *  - unwrap ApiResponse<T> → T, or throw a typed AppError
 *  - on 401, refresh-retry EXACTLY once, honoring the tri-state result
 */
export function createApi(deps: ApiDeps) {
  async function api<T>(path: string, options: ApiOptions = {}): Promise<T> {
    const { retryOn401 = true, headers, ...rest } = options;

    const res = await doFetch(path, headers, rest, deps.getToken());

    if (res.status === 401 && retryOn401) {
      const refreshed = await deps.refresh();
      if (refreshed === true) {
        const retry = await doFetch(path, headers, rest, deps.getToken());
        return unwrap<T>(retry);
      }
      if (refreshed === null) {
        // Unverifiable — keep last-known elsewhere; this call is uncertain, not dead.
        throw uncertain("Session could not be verified");
      }
      // refreshed === false → definitively expired.
      throw definitive("Session expired", 401);
    }

    return unwrap<T>(res);
  }

  return api;
}

async function doFetch(
  path: string,
  headers: HeadersInit | undefined,
  rest: RequestInit,
  token: string | null
): Promise<Response> {
  try {
    return await fetch(path, {
      ...rest,
      credentials: "include", // send the httpOnly refresh cookie; JS never reads it
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...headers,
      },
    });
  } catch (err) {
    // Transport failure (offline, DNS, CORS preflight) → uncertain, not definitive.
    throw uncertain("Network request failed", err);
  }
}

async function unwrap<T>(res: Response): Promise<T> {
  let body: ApiResponse<T> | null = null;
  try {
    body = (await res.json()) as ApiResponse<T>;
  } catch {
    // Non-JSON body.
    if (!res.ok) throw definitive(`Request failed (${res.status})`, res.status);
    throw definitive("Malformed response", res.status);
  }

  if (!res.ok || !body || body.success === false) {
    const msg = body?.error?.message ?? `Request failed (${res.status})`;
    // 5xx could be transient; but treat server-declared failures as definitive
    // unless you have a specific reason to retry. Keep it simple (KISS).
    throw definitive(msg, res.status);
  }

  return body.data as T;
}

export type Api = ReturnType<typeof createApi>;
export type { AppError };
