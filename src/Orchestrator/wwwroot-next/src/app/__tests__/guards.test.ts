import { describe, it, expect } from "vitest";
import { canAccessAdmin } from "../guards";
import type { AuthUser } from "../../auth/types";

const admin: AuthUser = { id: "a", walletAddress: "0xA", roles: ["User", "Admin"] };
const normal: AuthUser = { id: "u", walletAddress: "0xB", roles: ["User"] };

describe("canAccessAdmin (UX guard; server still enforces)", () => {
  it("true only for admins", () => {
    expect(canAccessAdmin(admin)).toBe(true);
  });
  it("false for normal users", () => {
    expect(canAccessAdmin(normal)).toBe(false);
  });
  it("false when no user (null/undefined)", () => {
    expect(canAccessAdmin(null)).toBe(false);
    expect(canAccessAdmin(undefined)).toBe(false);
  });
});
