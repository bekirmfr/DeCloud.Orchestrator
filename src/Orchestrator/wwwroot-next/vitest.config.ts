import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "jsdom", // tokenStore uses localStorage; AuthProvider uses React DOM
    globals: false, // we import { describe, it, expect } explicitly
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
