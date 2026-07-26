import { describe, it, expect } from "vitest";
import {
  shq, b64, validRepoUrl, validEnvKey, parseDotEnv,
  buildDeployConf, buildAppEnv, buildEnvironmentVariables,
} from "../repoDeploy";

describe("repoDeploy composition — byte-exact against repo-deploy.js", () => {
  it("shq single-quotes and escapes internal quotes", () => {
    expect(shq("main")).toBe("'main'");
    expect(shq("a'b")).toBe("'a'\\''b'");
  });

  it("b64 is unicode-safe (not bare btoa)", () => {
    expect(b64("hello")).toBe("aGVsbG8=");
    expect(b64("é")).toBe("w6k=");        // UTF-8 C3 A9, not latin1
    expect(b64("\n")).toBe("Cg==");
  });

  it("validRepoUrl accepts https and git@ forms, rejects others", () => {
    expect(validRepoUrl("https://github.com/owner/repo")).toBe(true);
    expect(validRepoUrl("git@github.com:owner/repo.git")).toBe(true);
    expect(validRepoUrl("github.com/owner/repo")).toBe(false);
    expect(validRepoUrl("https://github.com")).toBe(false);
  });

  it("validEnvKey enforces the shell-ident rule", () => {
    expect(validEnvKey("API_KEY")).toBe(true);
    expect(validEnvKey("_x")).toBe(true);
    expect(validEnvKey("1BAD")).toBe(false);
    expect(validEnvKey("has-dash")).toBe(false);
  });

  it("parseDotEnv skips blanks/comments, strips export, unwraps quotes", () => {
    const entries = parseDotEnv([
      "# comment",
      "",
      "export FOO=bar",
      'QUOTED="hello world"',
      "NOEQ",
    ].join("\n"));
    expect(entries).toEqual([
      { key: "FOO", value: "bar" },
      { key: "QUOTED", value: "hello world" },
    ]);
  });

  it("buildDeployConf shell-quotes each value with the trailing empty line", () => {
    expect(buildDeployConf({ sourceUrl: "https://github.com/o/r", sourceRef: "main", appPort: "3000", database: "postgres" }))
      .toBe("SOURCE_URL='https://github.com/o/r'\nSOURCE_REF='main'\nAPP_PORT='3000'\nDATABASE='postgres'\n");
  });

  it("buildAppEnv collapses newlines in values and ends with a newline", () => {
    expect(buildAppEnv({ FOO: "bar", MULTI: "a\nb" })).toBe("FOO=bar\nMULTI=a b\n");
    expect(buildAppEnv({})).toBe("\n");
  });

  it("buildEnvironmentVariables emits the blobs; DEPLOY_KEY_B64 only with a key", () => {
    const conf = { sourceUrl: "https://github.com/o/r", sourceRef: "HEAD", appPort: "8080", database: "none" };
    const noKey = buildEnvironmentVariables(conf, {});
    expect(Object.keys(noKey).sort()).toEqual(["APP_ENV_B64", "DEPLOY_CONF_B64"]);
    expect(noKey.DEPLOY_CONF_B64).toBe(b64(buildDeployConf(conf)));

    const withKey = buildEnvironmentVariables(conf, { FOO: "bar" }, "PRIVATEKEY");
    expect(withKey.DEPLOY_KEY_B64).toBe(b64("PRIVATEKEY\n"));   // trailing newline ensured
  });
});
