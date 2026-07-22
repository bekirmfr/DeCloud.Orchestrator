import { describe, it, expect } from "vitest";
import { resolveShellView } from "../resolveShellView";

describe("resolveShellView — status → surface (DESIGN §4/§5)", () => {
  it("NEEDS_CONNECT → connect gate", () => {
    expect(resolveShellView("NEEDS_CONNECT")).toEqual({ surface: "connect" });
  });

  it("NEEDS_AUTH → reauth gate (sign in)", () => {
    expect(resolveShellView("NEEDS_AUTH")).toEqual({ surface: "reauth", reason: "auth" });
  });

  it("ADDRESS_MISMATCH → reauth gate, reason=address-mismatch (fail closed)", () => {
    expect(resolveShellView("ADDRESS_MISMATCH")).toEqual({ surface: "reauth", reason: "address-mismatch" });
  });

  it("WRONG_NETWORK → app shown, network banner, escrow BLOCKED", () => {
    expect(resolveShellView("WRONG_NETWORK")).toEqual({ surface: "app", banner: "wrong-network", escrowBlocked: true });
  });

  it("UNCERTAIN → app shown with last-known, stale banner, NOT blocked", () => {
    expect(resolveShellView("UNCERTAIN")).toEqual({ surface: "app", banner: "stale", escrowBlocked: false });
  });

  it("READY → app, no banner, nothing blocked", () => {
    expect(resolveShellView("READY")).toEqual({ surface: "app", banner: "none", escrowBlocked: false });
  });

  it("only WRONG_NETWORK blocks escrow", () => {
    const blocked = (["READY", "UNCERTAIN", "WRONG_NETWORK"] as const).filter((s) => {
      const v = resolveShellView(s);
      return v.surface === "app" && v.escrowBlocked;
    });
    expect(blocked).toEqual(["WRONG_NETWORK"]);
  });
});
