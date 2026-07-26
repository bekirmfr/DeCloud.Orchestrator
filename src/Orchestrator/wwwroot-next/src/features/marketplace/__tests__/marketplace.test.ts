import { describe, it, expect } from "vitest";
import { buildTemplateQuery, shortWallet } from "../useMarketplace";

describe("shortWallet — truncates long hex addresses, passes short strings through", () => {
  it("truncates a wallet to 0x1234…cdef", () => {
    expect(shortWallet("0x1234567890abcdef1234567890abcdef12345678")).toBe("0x1234…5678");
  });
  it("leaves short display names/strings unchanged", () => {
    expect(shortWallet("alice")).toBe("alice");
    expect(shortWallet("0x1234")).toBe("0x1234");
  });
});

describe("buildTemplateQuery — emits only non-default filters", () => {
  it("is empty for a bare browse (and drops the default 'popular' sort)", () => {
    expect(buildTemplateQuery({})).toBe("");
    expect(buildTemplateQuery({ sortBy: "popular" })).toBe("");
    expect(buildTemplateQuery({ search: "" })).toBe("");
  });

  it("emits each set filter", () => {
    expect(buildTemplateQuery({ search: "postgres" })).toBe("?search=postgres");
    expect(buildTemplateQuery({ category: "databases" })).toBe("?category=databases");
    expect(buildTemplateQuery({ requiresGpu: true })).toBe("?requiresGpu=true");
    expect(buildTemplateQuery({ sortBy: "newest" })).toBe("?sortBy=newest");
  });

  it("combines and URL-encodes", () => {
    const qs = buildTemplateQuery({ search: "ml gpu", category: "ai", requiresGpu: true, sortBy: "rating" });
    expect(qs.startsWith("?")).toBe(true);
    expect(qs).toContain("search=ml+gpu");
    expect(qs).toContain("category=ai");
    expect(qs).toContain("requiresGpu=true");
    expect(qs).toContain("sortBy=rating");
  });
});
