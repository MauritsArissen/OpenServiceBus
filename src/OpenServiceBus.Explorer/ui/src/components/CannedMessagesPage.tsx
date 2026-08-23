import {
  ArrowLeftIcon, BookmarkIcon, CopyIcon, DownloadIcon, FileJsonIcon, PencilIcon, PlusIcon,
  RefreshCwIcon, SendIcon, Trash2Icon, UploadIcon,
} from "lucide-react";
import { useRef } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { VariableChips } from "@/components/variables/VariableChips";
import { VariableLegend } from "@/components/variables/VariableLegend";
import { explorerApi, type CannedMessage } from "@/lib/api";
import { useStore } from "@/store";

/** Full-page management view for the canned message library: create, edit, duplicate,
 *  delete and import. Reached from the sidebar's Canned messages entry; sending happens
 *  on an entity's Send tab, where the picker offers whatever applies there. */
export function CannedMessagesPage() {
  const store = useStore();
  const fileInput = useRef<HTMLInputElement>(null);

  const importFile = async (file: File) => {
    try {
      const parsed: unknown = JSON.parse(await file.text());
      const items = (Array.isArray(parsed) ? parsed : [parsed]) as CannedMessage[];
      if (items.length === 0 || items.some((m) => typeof m?.name !== "string" || m.name.trim() === "")) {
        toast.error("Import needs a JSON array of canned messages, each with a name.");
        return;
      }
      const summary = await explorerApi.importCanned(items, "skip");
      await store.refreshCanned();
      if (summary.skipped > 0) {
        toast.warning(`Imported ${summary.added}, skipped ${summary.skipped} existing`, {
          action: {
            label: "Replace existing",
            onClick: () => {
              void explorerApi.importCanned(items, "replace").then(async (r) => {
                await store.refreshCanned();
                toast.success(`Replaced ${r.replaced} canned message(s)`);
              });
            },
          },
        });
      } else {
        toast.success(`Imported ${summary.added} canned message(s)`);
      }
    } catch (e) {
      toast.error("Import failed: " + (e as Error).message);
    }
  };

  const duplicate = async (name: string) => {
    try {
      const copy = await explorerApi.duplicateCanned(name);
      await store.refreshCanned();
      toast.success(`Duplicated as '${copy.name}'`);
    } catch (e) {
      toast.error("Duplicate failed: " + (e as Error).message);
    }
  };

  const exportLibrary = async () => {
    try {
      const res = await fetch("/api/canned/export");
      if (!res.ok) throw new Error(res.statusText);
      const url = URL.createObjectURL(await res.blob());
      const a = document.createElement("a");
      a.href = url;
      a.download = "canned-messages.json";
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      toast.error("Export failed: " + (e as Error).message);
    }
  };

  const reloadFromFile = () =>
    store.setDialog({
      type: "confirm",
      title: "Reload the library from its file?",
      description: "Everything added or changed since the file was last written is discarded and the file's current contents (e.g. after a git pull) are loaded.",
      destructive: true,
      action: async () => {
        const res = await fetch("/api/canned/reset", { method: "POST" });
        if (!res.ok) throw new Error(res.statusText);
        await store.refreshCanned();
        toast.success("Library reloaded from file");
      },
    });

  const remove = (name: string) =>
    store.setDialog({
      type: "confirm",
      title: `Delete canned message '${name}'?`,
      description: "This removes it from the library for everyone using this Explorer.",
      destructive: true,
      action: async () => {
        await explorerApi.deleteCanned(name);
        await store.refreshCanned();
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
              <BookmarkIcon className="size-5 text-primary" /> Canned messages
            </h2>
            <p className="text-xs text-muted-foreground">
              Saved Send forms, replayable from any matching entity's Send tab. Shared by
              everyone using this Explorer.
            </p>
          </div>
          <VariableLegend />
        </div>

        <div className="flex flex-wrap gap-2">
          <Button onClick={() => store.setDialog({ type: "editCanned" })}>
            <PlusIcon /> New canned message
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
          {store.cannedFile?.configured && (
            <Button variant="outline" onClick={reloadFromFile}>
              <RefreshCwIcon /> Reload from file
            </Button>
          )}
        </div>

        <div className="flex items-start gap-2 rounded-md border bg-muted/40 p-2.5 text-xs text-muted-foreground">
          <FileJsonIcon className="mt-0.5 size-3.5 shrink-0" />
          {store.cannedFile?.configured ? (
            store.cannedFile.writable ? (
              <p>
                Persisted to{" "}
                {store.cannedFile.path ? <code className="font-mono">{store.cannedFile.path}</code> : "the configured library file"}
                {" "}- every change here writes back to it, so commit that file to share the
                library with your team.
              </p>
            ) : (
              <p>
                The configured library file is <span className="font-medium text-amber-600 dark:text-amber-400">read-only</span>
                {store.cannedFile.path && <> (<code className="font-mono">{store.cannedFile.path}</code>)</>}
                {" "}- changes made here live in memory until the Explorer restarts or reloads.
                Export and commit the JSON to make them stick.
              </p>
            )
          ) : (
            <p>
              In-memory only. Point <code className="font-mono">OSB_EXPLORER_CANNED_FILE</code> at a
              JSON file (or mount one in docker compose) to persist the library and commit it
              to git with your team.
            </p>
          )}
        </div>

        {store.canned.length === 0 ? (
          <Card>
            <CardContent className="py-10 text-center text-sm text-muted-foreground">
              Nothing saved yet. Create one here, or fill an entity's Send tab and hit
              "Save as canned".
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-3 sm:grid-cols-2">
            {store.canned.map((m) => (
              <Card key={m.name} className="min-w-0">
                <CardContent className="space-y-2.5 p-4">
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <p className="truncate font-mono text-sm font-semibold">{m.name}</p>
                      <p className="mt-0.5 truncate text-[11px] text-muted-foreground">
                        {m.targetEntity && m.targetEntity !== "*" ? (
                          <>only <span className="font-mono">{m.targetEntity}</span></>
                        ) : (
                          "any entity"
                        )}
                      </p>
                    </div>
                    <div className="flex shrink-0 gap-0.5">
                      <Button variant="ghost" size="icon" className="size-7" title="Edit"
                        onClick={() => store.setDialog({ type: "editCanned", edit: m })}>
                        <PencilIcon className="size-3.5" />
                      </Button>
                      <Button variant="ghost" size="icon" className="size-7" title="Duplicate"
                        onClick={() => void duplicate(m.name)}>
                        <CopyIcon className="size-3.5" />
                      </Button>
                      <Button variant="ghost" size="icon" className="size-7" title="Delete"
                        onClick={() => remove(m.name)}>
                        <Trash2Icon className="size-3.5 text-destructive" />
                      </Button>
                    </div>
                  </div>

                  {m.body && (
                    <pre className="line-clamp-3 overflow-hidden whitespace-pre-wrap break-all rounded-md bg-muted/60 p-2 font-mono text-[11px] text-muted-foreground">
                      {m.body}
                    </pre>
                  )}

                  <div className="flex flex-wrap items-center gap-1.5">
                    {(m.count ?? 1) > 1 && (
                      <Badge variant="muted" className="gap-1 text-[10px]">
                        <SendIcon className="size-3" /> {m.count} × {m.strategy === "PARALLEL" ? "parallel" : "at once"}
                      </Badge>
                    )}
                    {m.contentType && <Badge variant="muted" className="font-mono text-[10px]">{m.contentType}</Badge>}
                    {m.sessionId && <Badge variant="muted" className="text-[10px]">session</Badge>}
                    {m.scheduledDelaySeconds && <Badge variant="muted" className="text-[10px]">+{m.scheduledDelaySeconds}s</Badge>}
                    {m.properties && Object.keys(m.properties).length > 0 && (
                      <Badge variant="muted" className="text-[10px]">{Object.keys(m.properties).length} prop(s)</Badge>
                    )}
                  </div>

                  <VariableChips
                    fields={{
                      Body: m.body ?? "", MessageId: m.messageId ?? "", CorrelationId: m.correlationId ?? "",
                      Subject: m.subject ?? "", ReplyTo: m.replyTo ?? "", To: m.to ?? "",
                      SessionId: m.sessionId ?? "", PartitionKey: m.partitionKey ?? "",
                      ...Object.fromEntries(Object.entries(m.properties ?? {}).map(([k, v]) => [`prop ${k}`, v])),
                    }}
                  />
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>
    </main>
  );
}
