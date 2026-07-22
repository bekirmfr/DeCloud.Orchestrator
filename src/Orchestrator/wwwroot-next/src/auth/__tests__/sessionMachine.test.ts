import { describe, it, expect, vi } from "vitest";
import { sessionReducer, performRefresh } from "../sessionMachine";
import type { AuthUser, SessionState } from "../types";

const user: AuthUser = { id: "u1", walletAddress: "0xA", roles: ["User"] };
const authed: SessionState = { kind: "authenticated", token: "t1", address: "0xA", user };

describe("sessionReducer — pure transitions", () => {
  it("AUTH_START → authenticating", () => {
    expect(sessionReducer({ kind: "anonymous" }, { type: "AUTH_START" }).kind).toBe("authenticating");
  });

  it("AUTH_SUCCESS → authenticated with token/address/user", () => {
    const s = sessionReducer({ kind: "authenticating" }, { type: "AUTH_SUCCESS", token: "t", address: "0xA", user });
    expect(s).toMatchObject({ kind: "authenticated", token: "t", address: "0xA" });
  });

  it("REFRESH_UNVERIFIABLE keeps last-known as UNCERTAIN (never destroys state)", () => {
    const s = sessionReducer(authed, { type: "REFRESH_UNVERIFIABLE" });
    expect(s).toMatchObject({ kind: "uncertain", token: "t1", address: "0xA" });
  });

  it("REFRESH_EXPIRED → expired", () => {
    expect(sessionReducer(authed, { type: "REFRESH_EXPIRED" }).kind).toBe("expired");
  });

  it("REFRESH_OK from uncertain re-arms to authenticated with the new token", () => {
    const uncertain: SessionState = { kind: "uncertain", token: "old", address: "0xA", user };
    const s = sessionReducer(uncertain, { type: "REFRESH_OK", token: "new" });
    expect(s).toMatchObject({ kind: "authenticated", token: "new" });
  });

  it("SIGN_OUT → anonymous", () => {
    expect(sessionReducer(authed, { type: "SIGN_OUT" }).kind).toBe("anonymous");
  });
});

describe("performRefresh — tri-state contract", () => {
  it("token → true, sets token, emits REFRESH_OK", async () => {
    const dispatch = vi.fn();
    const setToken = vi.fn();
    const r = await performRefresh({ callRefresh: async () => ({ token: "n" }) }, dispatch, setToken);
    expect(r).toBe(true);
    expect(setToken).toHaveBeenCalledWith("n");
    expect(dispatch).toHaveBeenCalledWith({ type: "REFRESH_OK", token: "n" });
  });

  it("false → false, clears token, emits REFRESH_EXPIRED", async () => {
    const dispatch = vi.fn();
    const setToken = vi.fn();
    const r = await performRefresh({ callRefresh: async () => false }, dispatch, setToken);
    expect(r).toBe(false);
    expect(setToken).toHaveBeenCalledWith(null);
    expect(dispatch).toHaveBeenCalledWith({ type: "REFRESH_EXPIRED" });
  });

  it("null → null, does NOT clear token, emits REFRESH_UNVERIFIABLE", async () => {
    const dispatch = vi.fn();
    const setToken = vi.fn();
    const r = await performRefresh({ callRefresh: async () => null }, dispatch, setToken);
    expect(r).toBeNull();
    expect(setToken).not.toHaveBeenCalled();
    expect(dispatch).toHaveBeenCalledWith({ type: "REFRESH_UNVERIFIABLE" });
  });

  it("throw (transport failure) is treated as null/unverifiable, not expired", async () => {
    const dispatch = vi.fn();
    const setToken = vi.fn();
    const r = await performRefresh({ callRefresh: async () => { throw new Error("net"); } }, dispatch, setToken);
    expect(r).toBeNull();
    expect(dispatch).toHaveBeenCalledWith({ type: "REFRESH_UNVERIFIABLE" });
  });
});
