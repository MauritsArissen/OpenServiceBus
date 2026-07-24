import { TriangleAlertIcon, XIcon } from "lucide-react";
import { useEffect, useState } from "react";
import { fetchLatestRelease, isNewerVersion, type LatestRelease } from "@/lib/version";
import { useStore } from "@/store";

const SS_SHOWN = "osb-update-notice-shown";

export function UpdateBanner() {
  const { version } = useStore();
  const [release, setRelease] = useState<LatestRelease | null>(null);

  useEffect(() => {
    if (!version) return;
    if (sessionStorage.getItem(SS_SHOWN)) return;
    let cancelled = false;
    void fetchLatestRelease().then((latest) => {
      if (cancelled || !latest) return;
      if (isNewerVersion(latest.version, version)) {
        sessionStorage.setItem(SS_SHOWN, "1");
        setRelease(latest);
      }
    });
    return () => {
      cancelled = true;
    };
  }, [version]);

  if (!release) return null;

  return (
    <div className="fixed bottom-4 right-4 z-50 w-[min(92vw,20rem)] rounded-lg border border-amber-500/40 bg-amber-50 p-3 shadow-lg dark:bg-amber-950/50">
      <div className="flex items-start gap-2.5">
        <TriangleAlertIcon className="mt-0.5 size-4 shrink-0 text-amber-600 dark:text-amber-400" />
        <div className="min-w-0 flex-1">
          <div className="text-sm font-medium text-amber-800 dark:text-amber-200">Update available</div>
          <p className="mt-0.5 text-xs leading-relaxed text-amber-700 dark:text-amber-300/90">
            You're running v{version}. v{release.version} is the latest.{" "}
            <a
              href={release.url}
              target="_blank"
              rel="noreferrer"
              className="font-medium underline underline-offset-2"
            >
              Release notes
            </a>
          </p>
        </div>
        <button
          type="button"
          onClick={() => setRelease(null)}
          aria-label="Dismiss update notice"
          className="-mr-1 -mt-1 rounded p-1 text-amber-700/70 transition-colors hover:bg-amber-500/10 hover:text-amber-800 dark:text-amber-300/70 dark:hover:text-amber-200"
        >
          <XIcon className="size-4" />
        </button>
      </div>
    </div>
  );
}
