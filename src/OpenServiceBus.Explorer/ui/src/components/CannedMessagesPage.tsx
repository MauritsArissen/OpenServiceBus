import {
  ArrowLeftIcon, BookmarkIcon, CopyIcon, PencilIcon, PlusIcon, SendIcon, Trash2Icon, UploadIcon,
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
