import {
  ArrowLeftIcon, CheckCircle2Icon, CopyIcon, DownloadIcon, FileJsonIcon, GlobeIcon,
  PencilIcon, PlusIcon, RefreshCwIcon, Trash2Icon, UploadIcon,
} from "lucide-react";
import { useRef } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { explorerApi, type ExplorerEnvironment } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useStore } from "@/store";

/** Management page for Postman-style environments: named key/value sets, one active per
 *  browser, referenced in payloads as {{key}}. */
export function EnvironmentsPage() {
  const store = useStore();
  const fileInput = useRef<HTMLInputElement>(null);

  const importFile = async (file: File) => {
    try {
      const parsed: unknown = JSON.parse(await file.text());
      const items = (Array.isArray(parsed) ? parsed : [parsed]) as ExplorerEnvironment[];
      if (items.length === 0 || items.some((e) => typeof e?.name !== "string" || e.name.trim() === "")) {
        toast.error("Import needs Postman-style environments, each with a name.");
        return;
      }
      const summary = await explorerApi.importEnvironments(items, "skip");
      await store.refreshEnvironments();
      if (summary.skipped > 0) {
        toast.warning(`Imported ${summary.added}, skipped ${summary.skipped} existing`, {
          action: {
            label: "Replace existing",
            onClick: () => {
              void explorerApi.importEnvironments(items, "replace").then(async (r) => {
                await store.refreshEnvironments();
                toast.success(`Replaced ${r.replaced} environment(s)`);
              });
            },
          },
        });
      } else {
        toast.success(`Imported ${summary.added} environment(s)`);
      }
    } catch (e) {
      toast.error("Import failed: " + (e as Error).message);
    }
  };

  const exportLibrary = async () => {
    try {
      const res = await fetch("/api/environments/export");
      if (!res.ok) throw new Error(res.statusText);
      const url = URL.createObjectURL(await res.blob());
      const a = document.createElement("a");
      a.href = url;
      a.download = "environments.json";
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      toast.error("Export failed: " + (e as Error).message);
    }
  };

  const reloadFromFile = () =>
    store.setDialog({
      type: "confirm",
      title: "Reload environments from the file?",
      description: "Everything added or changed since the file was last written is discarded and the file's current contents are loaded.",
      destructive: true,
      action: async () => {
        const res = await fetch("/api/environments/reset", { method: "POST" });
        if (!res.ok) throw new Error(res.statusText);
        await store.refreshEnvironments();
        toast.success("Environments reloaded from file");
      },
    });

  const duplicate = async (name: string) => {
    try {
      const copy = await explorerApi.duplicateEnvironment(name);
      await store.refreshEnvironments();
      toast.success(`Duplicated as '${copy.name}'`);
    } catch (e) {
      toast.error("Duplicate failed: " + (e as Error).message);
    }
  };

  const remove = (name: string) =>
    store.setDialog({
      type: "confirm",
      title: `Delete environment '${name}'?`,
      description: "This removes it for everyone using this Explorer.",
      destructive: true,
      action: async () => {
        await explorerApi.deleteEnvironment(name);
        if (store.activeEnvironment === name) store.setActiveEnvironment(null);
        await store.refreshEnvironments();
        toast.success(`Deleted '${name}'`);
      },
    });

  return (
    <main className="min-w-0 flex-1 overflow-y-auto bg-muted/30">
      <div className="mx-auto max-w-5xl space-y-4 p-4 sm:p-6">
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="ghost" size="icon" onClick={() => store.setView("entity")} title="Back">
            <ArrowLeftIcon />
          </Button>
          <div className="min-w-0 flex-1">
            <h2 className="flex items-center gap-2 text-lg font-semibold">
              <GlobeIcon className="size-5 text-primary" /> Environments
            </h2>
            <p className="text-xs text-muted-foreground">
              Named value sets, Postman-style. One is active per browser; payloads
              reference values as {"{{key}}"} and resolve at send time.
            </p>
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <Button onClick={() => store.setDialog({ type: "editEnvironment" })}>
            <PlusIcon /> New environment
          </Button>
          <input
            ref={fileInput}
            type="file"
            accept=".json,application/json"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0];
              e.target.value = "";
              if (file) void importFile(file);
            }}
          />
          <Button variant="outline" onClick={() => fileInput.current?.click()}>
            <UploadIcon /> Import JSON
          </Button>
          <Button variant="outline" onClick={() => void exportLibrary()}>
            <DownloadIcon /> Export JSON
          </Button>
          {store.environmentsFile?.configured && (
            <Button variant="outline" onClick={reloadFromFile}>
              <RefreshCwIcon /> Reload from file
            </Button>
          )}
        </div>

        <div className="flex items-start gap-2 rounded-md border bg-muted/40 p-2.5 text-xs text-muted-foreground">
          <FileJsonIcon className="mt-0.5 size-3.5 shrink-0" />
          {store.environmentsFile?.configured ? (
            store.environmentsFile.writable ? (
              <p>
                Persisted to{" "}
                {store.environmentsFile.path ? <code className="font-mono">{store.environmentsFile.path}</code> : "the configured file"}
                {" "}- every change writes back, so commit that file to share environments with
                your team. Postman environment exports import directly.
              </p>
            ) : (
              <p>
                The configured environments file is <span className="font-medium text-amber-600 dark:text-amber-400">read-only</span>
                {" "}- changes live in memory until the Explorer restarts or reloads.
              </p>
            )
          ) : (
            <p>
              In-memory only. Point <code className="font-mono">OSB_EXPLORER_ENVIRONMENTS_FILE</code> at
              a JSON file (or mount one in docker compose) to persist and commit environments.
              Postman environment exports import directly.
            </p>
          )}
        </div>

        {store.environments.length === 0 ? (
          <Card>
            <CardContent className="py-10 text-center text-sm text-muted-foreground">
              No environments yet. Create one, or import a Postman environment export.
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-3 sm:grid-cols-2">
            {store.environments.map((e) => {
              const active = store.activeEnvironment === e.name;
              return (
                <Card key={e.name} className={cn("min-w-0", active && "border-primary/60")}>
                  <CardContent className="space-y-2.5 p-4">
                    <div className="flex items-start justify-between gap-2">
                      <div className="min-w-0">
                        <p className="flex items-center gap-1.5 truncate text-sm font-semibold">
                          {e.name}
                          {active && <Badge className="gap-1 text-[10px]"><CheckCircle2Icon className="size-3" /> active</Badge>}
                        </p>
                        <p className="mt-0.5 text-[11px] text-muted-foreground">
                          {e.values.filter((v) => v.enabled).length} enabled / {e.values.length} value(s)
                        </p>
                      </div>
                      <div className="flex shrink-0 gap-0.5">
                        <Button variant="ghost" size="icon" className="size-7" title="Edit"
                          onClick={() => store.setDialog({ type: "editEnvironment", edit: e })}>
                          <PencilIcon className="size-3.5" />
                        </Button>
                        <Button variant="ghost" size="icon" className="size-7" title="Duplicate"
                          onClick={() => void duplicate(e.name)}>
                          <CopyIcon className="size-3.5" />
                        </Button>
                        <Button variant="ghost" size="icon" className="size-7" title="Delete"
                          onClick={() => remove(e.name)}>
                          <Trash2Icon className="size-3.5 text-destructive" />
                        </Button>
                      </div>
                    </div>

                    {e.values.length > 0 && (
                      <div className="space-y-1 rounded-md bg-muted/60 p-2">
                        {e.values.slice(0, 5).map((v) => (
                          <div key={v.key} className={cn("flex gap-2 font-mono text-[11px]", !v.enabled && "opacity-40 line-through")}>
                            <span className="min-w-0 flex-1 truncate text-sky-600 dark:text-sky-400">{v.key}</span>
                            <span className="min-w-0 flex-[2] truncate text-muted-foreground">{v.value}</span>
                          </div>
                        ))}
                        {e.values.length > 5 && (
                          <p className="text-[10px] text-muted-foreground">+{e.values.length - 5} more</p>
                        )}
                      </div>
                    )}

                    <Button
                      variant={active ? "outline" : "default"}
                      size="sm"
                      className="w-full"
                      onClick={() => store.setActiveEnvironment(active ? null : e.name)}
                    >
                      {active ? "Deactivate" : "Set active"}
                    </Button>
                  </CardContent>
                </Card>
              );
            })}
          </div>
        )}
      </div>
    </main>
  );
}
