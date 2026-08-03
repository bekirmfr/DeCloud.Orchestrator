import { describe, it, expect } from "vitest";
import { emptyForm, emptyPort, emptyVar, toPayload, fromTemplate, enumNum } from "../templateForm";
import type { VmTemplate } from "../../deploy/deploySubmit";

describe("templateForm mapping", () => {
  it("enumNum accepts a number or an enum name", () => {
    expect(enumNum(2, 0)).toBe(2);
    expect(enumNum("Private", 0)).toBe(1);
    expect(enumNum("Unmetered", 0)).toBe(3);
    expect(enumNum(undefined, 3)).toBe(3);
  });

  it("toPayload maps specs to bytes and splits/trims tags", () => {
    const p = toPayload({ ...emptyForm, name: "App", slug: "app", recMemMb: 2048, recDiskGb: 20, tags: "web, node ,, api" });
    expect(p.name).toBe("App");
    expect(p.tags).toEqual(["web", "node", "api"]);
    const rec = p.recommendedSpec as Record<string, number>;
    expect(rec.memoryBytes).toBe(2048 * 1024 * 1024);
    expect(rec.diskBytes).toBe(20 * 1024 ** 3);
  });

  it("fromTemplate round-trips spec bytes back to MB/GB", () => {
    const t = { id: "1", slug: "s", name: "N", recommendedSpec: { virtualCpuCores: 4, memoryBytes: 2048 * 1024 ** 2, diskBytes: 20 * 1024 ** 3 } } as unknown as VmTemplate;
    const f = fromTemplate(t);
    expect(f.recCpu).toBe(4);
    expect(f.recMemMb).toBe(2048);
    expect(f.recDiskGb).toBe(20);
  });

  it("toPayload drops empty env/port/variable rows", () => {
    const p = toPayload({
      ...emptyForm, name: "A", slug: "a",
      envVars: [{ key: "", value: "x" }, { key: "K", value: "v" }],
      ports: [{ ...emptyPort, protocol: "tcp" }],
      variables: [{ ...emptyVar }],
    });
    expect(p.defaultEnvironmentVariables).toEqual({ K: "v" });
    expect(p.exposedPorts).toEqual([]);
    expect(p.variables).toEqual([]);
  });

  it("toPayload maps the form's cloud-init to roleCloudInit (composed over base server-side)", () => {
    const p = toPayload({ ...emptyForm, name: "A", slug: "a", cloudInitTemplate: "#cloud-config\npackages:\n  - nginx\n" });
    expect(p.roleCloudInit).toBe("#cloud-config\npackages:\n  - nginx\n");
    expect(p.cloudInitTemplate).toBeUndefined();
  });

  it("fromTemplate loads roleCloudInit into the editor, with a fallback to cloudInitTemplate for legacy rows", () => {
    const authored = { id: "1", slug: "s", name: "N", roleCloudInit: "#role", cloudInitTemplate: "#composed" } as unknown as VmTemplate;
    expect(fromTemplate(authored).cloudInitTemplate).toBe("#role");
    const legacy = { id: "2", slug: "s2", name: "N2", cloudInitTemplate: "#legacy" } as unknown as VmTemplate;
    expect(fromTemplate(legacy).cloudInitTemplate).toBe("#legacy");
  });

  it("fromTemplate hides platform-managed base vars from the editor", () => {
    const t = { id: "1", slug: "s", name: "N", variables: [
      { name: "SITE_TITLE", kind: 0 }, { name: "CA_PUBLIC_KEY", kind: 0 }, { name: "VM_ID", kind: 0 },
    ] } as unknown as VmTemplate;
    expect(fromTemplate(t).variables.map((v) => v.name)).toEqual(["SITE_TITLE"]);
  });
});
