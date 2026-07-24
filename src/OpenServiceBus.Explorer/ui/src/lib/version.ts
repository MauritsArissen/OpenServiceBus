// Running-version display + "newer release available" check against GitHub.

const REPO = "mauritsarissen/OpenServiceBus";

export type LatestRelease = { version: string; url: string };

/** Parse a semver-ish string ("1.2.10", "v1.2.10", "1.2.10+build") into [major, minor, patch]. */
function parseSemver(v: string): [number, number, number] | null {
  const m = /^v?(\d+)\.(\d+)\.(\d+)/.exec(v.trim());
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

/** True when `latest` is a strictly higher release than `current`. False if either is unparseable. */
export function isNewerVersion(latest: string, current: string): boolean {
  const a = parseSemver(latest);
  const b = parseSemver(current);
  if (!a || !b) return false;
  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) return a[i] > b[i];
  }
  return false;
}

/**
 * Fetch the latest published release from GitHub. The REST API is CORS-enabled, so this runs
 * straight from the browser. Returns null on any failure (offline, rate-limited, no releases) -
 * the update check just stays quiet in that case.
 */
export async function fetchLatestRelease(): Promise<LatestRelease | null> {
  try {
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!res.ok) return null;
    const json = (await res.json()) as { tag_name?: string; html_url?: string };
    if (!json.tag_name) return null;
    return {
      version: json.tag_name.replace(/^v/, ""),
      url: json.html_url ?? `https://github.com/${REPO}/releases/latest`,
    };
  } catch {
    return null;
  }
}
