import { describe, it, expect } from "vitest";
import { validateSshKey } from "../AddKeyModal";

describe("validateSshKey (pure)", () => {
  it("rejects empty name", () => {
    expect(validateSshKey({ name: "  ", publicKey: "ssh-ed25519 AAAAC3..." })).toMatchObject({ ok: false, field: "name" });
  });

  it("rejects a non-key blob", () => {
    expect(validateSshKey({ name: "laptop", publicKey: "not a key" })).toMatchObject({ ok: false, field: "publicKey" });
  });

  it("accepts a well-formed ed25519 key", () => {
    expect(validateSshKey({ name: "laptop", publicKey: "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 me@host" })).toEqual({ ok: true });
  });

  it("accepts rsa and ecdsa too", () => {
    expect(validateSshKey({ name: "a", publicKey: "ssh-rsa AAAAB3Nza..." }).ok).toBe(true);
    expect(validateSshKey({ name: "b", publicKey: "ecdsa-sha2-nistp256 AAAAE2Vj..." }).ok).toBe(true);
  });
});
