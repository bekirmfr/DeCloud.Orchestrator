import { describe, it, expect, vi, beforeEach } from "vitest";
import { createApi } from "../../api/client";
import type { AppError } from "../../api/errors";

// These tests pin the §5 behavior of the API boundary. Bodies are written; if a
// helper (mockFetch) needs adjusting to your fetch polyfill, do that — the CASES
// are the spec and should not change.

function mockFetch(sequence: Array<{ status: number; body?: unknown; throw?: boolean }>) {
  const calls = sequence.slice();
  return vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
    const next = calls.shift();
    if (!next) throw new Error("fetch called more times than expected");
    if (next.throw) throw new TypeError("Failed to fetch");
    return {
      ok: next.status >= 200 && next.status < 300,
      status: next.status,
      json: async () => next.body,
    } as Response;
  });
}

describe("api() — §5 boundary behavior", () => {
  beforeEach(() => vi.restoreAllMocks());

  it("unwraps ApiResponse<T>.data on success", async () => {
    vi.stubGlobal("fetch", mockFetch([{ status: 200, body: { success: true, data: { hello: "world" } } }]));
    const api = createApi({ getToken: () => "tok", refresh: async () => true });
    await expect(api<{ hello: string }>("/api/x")).resolves.toEqual({ hello: "world" });
  });

  it("attaches the bearer token and credentials:include", async () => {
    const f = mockFetch([{ status: 200, body: { success: true, data: 1 } }]);
    vi.stubGlobal("fetch", f);
    const api = createApi({ getToken: () => "tok", refresh: async () => true });
    await api("/api/x");
    const init = f.mock.calls[0]![1] as RequestInit;
    expect(init.credentials).toBe("include");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer tok");
  });

  it("on 401, refresh===true → retries once and succeeds", async () => {
    vi.stubGlobal("fetch", mockFetch([
      { status: 401, body: { success: false, error: { message: "expired" } } },
      { status: 200, body: { success: true, data: "ok" } },
    ]));
    const refresh = vi.fn(async () => true as const);
    const api = createApi({ getToken: () => "tok", refresh });
    await expect(api<string>("/api/x")).resolves.toBe("ok");
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("on 401, refresh===false → throws DEFINITIVE (session expired), no retry", async () => {
    vi.stubGlobal("fetch", mockFetch([{ status: 401, body: { success: false } }]));
    const api = createApi({ getToken: () => "tok", refresh: async () => false });
    await expect(api("/api/x")).rejects.toMatchObject({ kind: "definitive", status: 401 } as Partial<AppError>);
  });

  it("on 401, refresh===null → throws UNCERTAIN (do not destroy state)", async () => {
    vi.stubGlobal("fetch", mockFetch([{ status: 401, body: { success: false } }]));
    const api = createApi({ getToken: () => "tok", refresh: async () => null });
    await expect(api("/api/x")).rejects.toMatchObject({ kind: "uncertain" } as Partial<AppError>);
  });

  it("refresh-retry happens EXACTLY once (a second 401 is not retried again)", async () => {
    vi.stubGlobal("fetch", mockFetch([
      { status: 401, body: { success: false } },
      { status: 401, body: { success: false } },
    ]));
    const refresh = vi.fn(async () => true as const);
    const api = createApi({ getToken: () => "tok", refresh });
    await expect(api("/api/x")).rejects.toMatchObject({ kind: "definitive" } as Partial<AppError>);
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("transport failure → UNCERTAIN (not definitive)", async () => {
    vi.stubGlobal("fetch", mockFetch([{ status: 0, throw: true }]));
    const api = createApi({ getToken: () => null, refresh: async () => true });
    await expect(api("/api/x")).rejects.toMatchObject({ kind: "uncertain" } as Partial<AppError>);
  });

  it("server-declared failure (success:false, 200) → DEFINITIVE with message", async () => {
    vi.stubGlobal("fetch", mockFetch([{ status: 200, body: { success: false, error: { message: "nope" } } }]));
    const api = createApi({ getToken: () => "t", refresh: async () => true });
    await expect(api("/api/x")).rejects.toMatchObject({ kind: "definitive", message: "nope" } as Partial<AppError>);
  });
});