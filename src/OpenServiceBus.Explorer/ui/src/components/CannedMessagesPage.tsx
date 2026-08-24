import {
  ArrowLeftIcon, BookmarkIcon, CopyIcon, DownloadIcon, PlusIcon, RefreshCwIcon,
  Trash2Icon, UploadIcon, XIcon,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { HighlightedBodyEditor } from "@/components/variables/HighlightedBodyEditor";
import { VariableChips } from "@/components/variables/VariableChips";
import { VariableLegend } from "@/components/variables/VariableLegend";
import { explorerApi, type CannedMessage } from "@/lib/api";
import { useMediaQuery } from "@/lib/useMediaQuery";
import { cn } from "@/lib/utils";
import { useStore } from "@/store";
import { FileHint } from "./EnvironmentsPage";

/** Master-detail management for the canned message library: a row list on the left, an
 *  inline full-form editor on the right (no modals). List first on phones. */
export function CannedMessagesPage() {
  const store = useStore();
  const desktop = useMediaQuery("(min-width: 768px)");
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const selected = creating ? null : store.canned.find((m) => m.name === selectedName) ?? null;
  const editorOpen = creating || selected !== null;

  useEffect(() => {
    if (desktop && !creating && selected === null && store.canned.length > 0) {
      setSelectedName(store.canned[0].name);
    }
  }, [desktop, creating, selected, store.canned]);

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
        setSelectedName(null);
        await store.refreshCanned();
        toast.success("Library reloaded from file");
      },
    });

  return (
    <main className="min-w-0 flex-1 overflow-y-auto bg-muted/30">
      <div className="mx-auto max-w-6xl space-y-3 p-4 sm:p-6">
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="ghost" size="icon" onClick={() => store.setView("entity")} title="Back to entities">
            <ArrowLeftIcon />
          </Button>
          <div className="min-w-0 flex-1">
            <h2 className="flex items-center gap-2 text-lg font-semibold">
              <BookmarkIcon className="size-5 text-primary" /> Canned messages
            </h2>
            <p className="text-xs text-muted-foreground">
              Saved Send forms, replayable from any matching entity's Send tab.
            </p>
          </div>
          <div className="flex w-full flex-wrap gap-2 md:w-auto">
            <Button size="sm" onClick={() => { setCreating(true); setSelectedName(null); }}>
              <PlusIcon /> New
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
            <Button variant="outline" size="sm" onClick={() => fileInput.current?.click()} title="Import JSON">
              <UploadIcon /> <span className="hidden sm:inline">Import</span>
            </Button>
            <Button variant="outline" size="sm" onClick={() => void exportLibrary()} title="Export JSON">
              <DownloadIcon /> <span className="hidden sm:inline">Export</span>
            </Button>
            {store.cannedFile?.configured && (
              <Button variant="outline" size="sm" onClick={reloadFromFile} title="Reload from file">
                <RefreshCwIcon /> <span className="hidden sm:inline">Reload</span>
              </Button>
            )}
            <VariableLegend />
          </div>
        </div>

        <FileHint kind="library" info={store.cannedFile} settingName="OSB_EXPLORER_CANNED_FILE" />

        <div className="flex gap-4">
          <div className={cn("w-full shrink-0 space-y-0.5 md:w-64", editorOpen && "hidden md:block")}>
            {store.canned.length === 0 && !creating && (
              <p className="rounded-md border border-dashed p-4 text-center text-xs text-muted-foreground">
                Nothing saved yet. Create one here, or fill an entity's Send tab and hit
                "Save as canned".
              </p>
            )}
            {store.canned.map((m) => {
              const current = !creating && selectedName === m.name;
              return (
                <button
                  key={m.name}
                  className={cn(
                    "group relative flex h-10 w-full items-center gap-2 rounded-md px-2.5 text-left hover:bg-accent",
                    current && "bg-accent",
                  )}
                  onClick={() => { setCreating(false); setSelectedName(m.name); }}
                >
                  {current && <span className="absolute inset-y-2 left-0 w-0.5 rounded-full bg-primary" />}
                  <BookmarkIcon className="size-4 shrink-0 text-muted-foreground" />
                  <span className={cn("min-w-0 flex-1 truncate font-mono text-[13px]", current && "font-medium")}>{m.name}</span>
                  <Badge variant="muted" className="max-w-20 truncate font-mono text-[10px]">
                    {m.targetEntity && m.targetEntity !== "*" ? m.targetEntity : "any"}
                  </Badge>
                </button>
              );
            })}
          </div>

          <div className={cn("min-w-0 flex-1", !editorOpen && "hidden md:block")}>
            {creating ? (
              <CannedEditor
                key="@new"
                onClose={() => setCreating(false)}
                onSaved={(name) => { setCreating(false); setSelectedName(name); }}
              />
            ) : selected ? (
              <CannedEditor
                key={selected.name}
                edit={selected}
                onClose={() => setSelectedName(null)}
                onSaved={(name) => setSelectedName(name)}
                onDeleted={() => setSelectedName(null)}
              />
            ) : (
              <p className="rounded-md border border-dashed p-8 text-center text-sm text-muted-foreground">
                Select a canned message to edit it, or create a new one.
              </p>
            )}
          </div>
        </div>
      </div>
    </main>
  );
}

type PropRow = { key: string; value: string };

const ANY = "*";

/** Inline full-form editor: every Send field plus name and target scope, with explicit
 *  Save/Revert and dirty tracking. */
function CannedEditor({
  edit, onClose, onSaved, onDeleted,
}: {
  edit?: CannedMessage;
  onClose: () => void;
  onSaved: (name: string) => void;
  onDeleted?: () => void;
}) {
  const store = useStore();
  const [name, setName] = useState(edit?.name ?? "");
  const [target, setTarget] = useState(edit?.targetEntity && edit.targetEntity !== ANY ? edit.targetEntity : ANY);
  const [body, setBody] = useState(edit?.body ?? "");
  const [messageId, setMessageId] = useState(edit?.messageId ?? "");
  const [correlationId, setCorrelationId] = useState(edit?.correlationId ?? "");
  const [subject, setSubject] = useState(edit?.subject ?? "");
  const [contentType, setContentType] = useState(edit?.contentType ?? "");
  const [replyTo, setReplyTo] = useState(edit?.replyTo ?? "");
  const [to, setTo] = useState(edit?.to ?? "");
  const [sessionId, setSessionId] = useState(edit?.sessionId ?? "");
  const [partitionKey, setPartitionKey] = useState(edit?.partitionKey ?? "");
  const [ttl, setTtl] = useState(edit?.timeToLiveSeconds ? String(edit.timeToLiveSeconds) : "");
  const [delay, setDelay] = useState(edit?.scheduledDelaySeconds ? String(edit.scheduledDelaySeconds) : "");
  const [props, setProps] = useState<PropRow[]>(
    Object.entries(edit?.properties ?? {}).map(([key, value]) => ({ key, value })),
  );
  const [count, setCount] = useState(String(edit?.count ?? 1));
  const [strategy, setStrategy] = useState(edit?.strategy === "PARALLEL" ? "PARALLEL" : "ATONCE");
  const [busy, setBusy] = useState(false);

  const targets = [...store.queues.map((q) => q.name), ...store.topics.map((t) => t.name)];
  if (target !== ANY && !targets.includes(target)) targets.unshift(target);

  const orNull = (v: string) => (v.trim() === "" ? null : v);
  const intOrNull = (v: string) => {
    const p = parseInt(v, 10);
    return Number.isFinite(p) && p > 0 ? p : null;
  };

  const build = (): CannedMessage => {
    const collected: Record<string, string> = {};
    for (const r of props) {
      const k = r.key.trim();
      if (k) collected[k] = r.value;
    }
    return {
      name: name.trim(),
      targetEntity: target === ANY ? ANY : target,
      body: orNull(body),
      messageId: orNull(messageId),
      correlationId: orNull(correlationId),
      subject: orNull(subject),
      contentType: orNull(contentType),
      replyTo: orNull(replyTo),
      to: orNull(to),
      sessionId: orNull(sessionId),
      partitionKey: orNull(partitionKey),
      timeToLiveSeconds: intOrNull(ttl),
      scheduledDelaySeconds: intOrNull(delay),
      properties: Object.keys(collected).length > 0 ? collected : null,
      count: Math.max(1, parseInt(count, 10) || 1),
      strategy,
    };
  };

  const dirty = edit ? JSON.stringify(build()) !== JSON.stringify(edit) : true;

  const save = async () => {
    if (name.trim() === "") return toast.error("A name is required.");
    setBusy(true);
    try {
      const message = build();
      if (edit) {
        await explorerApi.updateCanned(edit.name, message);
      } else {
        await explorerApi.createCanned(message);
      }
      toast.success(`Saved '${message.name}'`);
      await store.refreshCanned();
      onSaved(message.name);
    } catch (e) {
      toast.error("Save failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const duplicate = async () => {
    if (!edit) return;
    try {
      const copy = await explorerApi.duplicateCanned(edit.name);
      await store.refreshCanned();
      onSaved(copy.name);
      toast.success(`Duplicated as '${copy.name}'`);
    } catch (e) {
      toast.error("Duplicate failed: " + (e as Error).message);
    }
  };

  const revert = () => {
    if (!edit) return;
    setName(edit.name);
    setTarget(edit.targetEntity && edit.targetEntity !== ANY ? edit.targetEntity : ANY);
    setBody(edit.body ?? "");
    setMessageId(edit.messageId ?? "");
    setCorrelationId(edit.correlationId ?? "");
    setSubject(edit.subject ?? "");
    setContentType(edit.contentType ?? "");
    setReplyTo(edit.replyTo ?? "");
    setTo(edit.to ?? "");
    setSessionId(edit.sessionId ?? "");
    setPartitionKey(edit.partitionKey ?? "");
    setTtl(edit.timeToLiveSeconds ? String(edit.timeToLiveSeconds) : "");
    setDelay(edit.scheduledDelaySeconds ? String(edit.scheduledDelaySeconds) : "");
    setProps(Object.entries(edit.properties ?? {}).map(([key, value]) => ({ key, value })));
    setCount(String(edit.count ?? 1));
    setStrategy(edit.strategy === "PARALLEL" ? "PARALLEL" : "ATONCE");
  };

  const remove = () => {
    if (!edit) return;
    store.setDialog({
      type: "confirm",
      title: `Delete canned message '${edit.name}'?`,
      description: "This removes it from the library for everyone using this Explorer.",
      destructive: true,
      action: async () => {
        await explorerApi.deleteCanned(edit.name);
        await store.refreshCanned();
        onDeleted?.();
        toast.success(`Deleted '${edit.name}'`);
      },
    });
  };

  return (
    <div className="rounded-lg border bg-background">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2.5">
        <Button variant="ghost" size="icon" className="-ml-2 size-7 md:hidden" onClick={onClose} title="Back to list">
          <ArrowLeftIcon className="size-4" />
        </Button>
        <span className="min-w-0 flex-1 truncate font-mono text-sm font-semibold">
          {edit ? edit.name : "New canned message"}
          {dirty && <span className="ml-1.5 align-middle font-sans text-[10px] font-normal text-amber-500">● unsaved</span>}
        </span>
        {edit && (
          <div className="flex items-center gap-1">
            <Button variant="ghost" size="icon" className="size-7" title="Duplicate" onClick={() => void duplicate()}>
              <CopyIcon className="size-3.5" />
            </Button>
            <Button variant="ghost" size="icon" className="size-7" title="Delete" onClick={remove}>
              <Trash2Icon className="size-3.5 text-destructive" />
            </Button>
          </div>
        )}
      </div>

      <div className="space-y-4 p-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1">
            <Label>Name</Label>
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. order-created" />
          </div>
          <div className="space-y-1">
            <Label>Available on</Label>
            <Select value={target} onValueChange={setTarget}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any entity</SelectItem>
                {targets.map((t) => (
                  <SelectItem key={t} value={t}>{t}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>

        <div className="space-y-1">
          <Label>Body</Label>
          <HighlightedBodyEditor value={body} onChange={setBody} placeholder='{"orderId": "{{$guid}}"}' env={store.activeEnvValues} />
          <VariableChips
            fields={{
              Body: body, MessageId: messageId, CorrelationId: correlationId, Subject: subject,
              ReplyTo: replyTo, To: to, SessionId: sessionId, PartitionKey: partitionKey,
              ...Object.fromEntries(props.filter((r) => r.key.trim()).map((r) => [`prop ${r.key}`, r.value])),
            }}
            env={store.activeEnvValues}
            className="pt-1"
          />
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <Field label="MessageId"><Input value={messageId} onChange={(e) => setMessageId(e.target.value)} placeholder="(auto)" className="font-mono text-xs" /></Field>
          <Field label="CorrelationId"><Input value={correlationId} onChange={(e) => setCorrelationId(e.target.value)} className="font-mono text-xs" /></Field>
          <Field label="Subject"><Input value={subject} onChange={(e) => setSubject(e.target.value)} /></Field>
          <Field label="ContentType"><Input value={contentType} onChange={(e) => setContentType(e.target.value)} placeholder="application/json" /></Field>
          <Field label="ReplyTo"><Input value={replyTo} onChange={(e) => setReplyTo(e.target.value)} /></Field>
          <Field label="To"><Input value={to} onChange={(e) => setTo(e.target.value)} /></Field>
          <Field label="SessionId"><Input value={sessionId} onChange={(e) => setSessionId(e.target.value)} /></Field>
          <Field label="PartitionKey"><Input value={partitionKey} onChange={(e) => setPartitionKey(e.target.value)} /></Field>
          <Field label="TTL (seconds)"><Input type="number" min={1} value={ttl} onChange={(e) => setTtl(e.target.value)} placeholder="∞" /></Field>
          <Field label="Schedule delay (seconds)"><Input type="number" min={1} value={delay} onChange={(e) => setDelay(e.target.value)} placeholder="send now" /></Field>
          <Field label="Copies"><Input type="number" min={1} value={count} onChange={(e) => setCount(e.target.value)} /></Field>
          <Field label="Strategy">
            <Select value={strategy} onValueChange={setStrategy}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="ATONCE">AT ONCE</SelectItem>
                <SelectItem value="PARALLEL">PARALLEL</SelectItem>
              </SelectContent>
            </Select>
          </Field>
        </div>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Application properties</Label>
            <Button variant="outline" size="sm" onClick={() => setProps((p) => [...p, { key: "", value: "" }])}>
              <PlusIcon /> Add row
            </Button>
          </div>
          {props.map((row, i) => (
            <div key={i} className="flex gap-2">
              <Input
                value={row.key} placeholder="key"
                onChange={(e) => setProps((p) => p.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))}
                className="font-mono text-xs"
              />
              <Input
                value={row.value} placeholder="value"
                onChange={(e) => setProps((p) => p.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))}
                className="font-mono text-xs"
              />
              <Button variant="ghost" size="icon" className="size-7 shrink-0" onClick={() => setProps((p) => p.filter((_, j) => j !== i))}>
                <XIcon className="size-3.5" />
              </Button>
            </div>
          ))}
        </div>

        <div className="flex justify-end gap-2 border-t pt-3">
          {edit ? (
            <Button variant="outline" disabled={!dirty || busy} onClick={revert}>
              Revert
            </Button>
          ) : (
            <Button variant="outline" onClick={onClose}>Cancel</Button>
          )}
          <Button onClick={() => void save()} disabled={busy || (edit !== undefined && !dirty)}>
            {busy ? "Saving…" : "Save"}
          </Button>
        </div>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1">
      <Label>{label}</Label>
      {children}
    </div>
  );
}
