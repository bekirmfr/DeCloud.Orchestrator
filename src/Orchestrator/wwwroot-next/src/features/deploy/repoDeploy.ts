// Byte-exact port of the composition in the legacy repo-deploy.js. The VM's
// provision.sh dot-sources these payloads, so the format is a hard contract —
// a byte off fails the build closed. Kept as pure functions so the composition
// is unit-tested against the legacy output.

/** Shell single-quote: ' → '\'' so a value can never escape its quotes. */
export function shq(v: string): string {
  return "'" + String(v).replace(/'/g, "'\\''") + "'";
}

/** Unicode-safe base64 — plain btoa chokes on non-latin1. Chunked to stay under
 *  String.fromCharCode's argument limit. */
export function b64(str: string): string {
  const bytes = new TextEncoder().encode(str);
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(bin);
}

/** https://host/owner/repo or git@host:owner/repo. */
export function validRepoUrl(url: string): boolean {
  return /^https:\/\/[^\s/]+\/\S+$/.test(url) || /^git@[^\s:]+:\S+$/.test(url);
}

/** Env var name: letters/digits/underscore, no leading digit. (PORT is rejected
 *  separately — it comes from the App-port field.) */
export function validEnvKey(key: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(key);
}

export interface EnvEntry { key: string; value: string }

/** Parse a pasted .env blob: skip blanks/comments, strip `export `, split on the
 *  first `=`, and unwrap a single layer of matching surrounding quotes. */
export function parseDotEnv(text: string): EnvEntry[] {
  const out: EnvEntry[] = [];
  for (let line of text.split(/\r?\n/)) {
    line = line.trim();
    if (!line || line.startsWith("#")) continue;
    if (line.startsWith("export ")) line = line.slice(7).trim();
    const eq = line.indexOf("=");
    if (eq <= 0) continue;
    const key = line.slice(0, eq).trim();
    let value = line.slice(eq + 1).trim();
    if ((value.startsWith('"') && value.endsWith('"')) ||
        (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }
    out.push({ key, value });
  }
  return out;
}

export interface RepoConf {
  sourceUrl: string;
  sourceRef: string;
  appPort: string;
  database: string;
}

/** DEPLOY_CONF block (pre-base64). The trailing empty line is intentional. */
export function buildDeployConf(v: RepoConf): string {
  return [
    `SOURCE_URL=${shq(v.sourceUrl)}`,
    `SOURCE_REF=${shq(v.sourceRef)}`,
    `APP_PORT=${shq(v.appPort)}`,
    `DATABASE=${shq(v.database)}`,
    "",
  ].join("\n");
}

/** app.env file (pre-base64). Newlines inside a value collapse to spaces so one
 *  value can't smuggle a second variable; trailing newline. */
export function buildAppEnv(env: Record<string, string>): string {
  return Object.entries(env)
    .map(([k, val]) => `${k}=${String(val).replace(/\r?\n/g, " ")}`)
    .join("\n") + "\n";
}

/** The environmentVariables payload: the three base64-armored blobs. DEPLOY_KEY_B64
 *  is present only when a deploy key was supplied. */
export function buildEnvironmentVariables(
  conf: RepoConf,
  env: Record<string, string>,
  deployKey?: string,
): Record<string, string> {
  const out: Record<string, string> = {
    DEPLOY_CONF_B64: b64(buildDeployConf(conf)),
    APP_ENV_B64: b64(buildAppEnv(env)),
  };
  if (deployKey) {
    out.DEPLOY_KEY_B64 = b64(deployKey.endsWith("\n") ? deployKey : deployKey + "\n");
  }
  return out;
}
