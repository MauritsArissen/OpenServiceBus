import {
  ArrowLeftIcon, CheckCircle2Icon, CopyIcon, DownloadIcon, FileJsonIcon, GlobeIcon,
  PlusIcon, RefreshCwIcon, Trash2Icon, UploadIcon, XIcon,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { explorerApi, type EnvironmentValue, type ExplorerEnvironment } from "@/lib/api";
import { useMediaQuery } from "@/lib/useMediaQuery";
import { cn } from "@/lib/utils";
import { useStore } from "@/store";

/** Master-detail management for Postman-style environments: a row list on the left, an
 *  inline editor on the right (no modals). On phones: list first, editor with a back
 *  button once a row is picked. */
export function EnvironmentsPage() {
  const store = useStore();
  const desktop = useMediaQuery("(min-width: 768px)");
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const selected = creating ? null : store.environments.find((e) => e.name === selectedName) ?? null;
  const editorOpen = creating || selected !== null;

  useEffect(() => {
    if (desktop && !creating && selected === null && store.environments.length > 0) {
      setSelectedName(store.environments[0].name);
    }
  }, [desktop, creating, selected, store.environments]);

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
        setSelectedName(null);
        await store.refreshEnvironments();
        toast.success("Environments reloaded from file");
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
              <GlobeIcon className="size-5 text-primary" /> Environments
            </h2>
            <p className="text-xs text-muted-foreground">
              Named value sets, Postman-style. One is active per browser; payloads
              reference values as {"{{key}}"}.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
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
            <Button variant="outline" size="sm" onClick={() => fileInput.current?.click()} title="Import JSON (Postman exports work)">
              <UploadIcon /> <span className="hidden sm:inline">Import</span>
            </Button>
            <Button variant="outline" size="sm" onClick={() => void exportLibrary()} title="Export JSON">
              <DownloadIcon /> <span className="hidden sm:inline">Export</span>
            </Button>
            {store.environmentsFile?.configured && (
              <Button variant="outline" size="sm" onClick={reloadFromFile} title="Reload from file">
                <RefreshCwIcon /> <span className="hidden sm:inline">Reload</span>
              </Button>
            )}
          </div>
        </div>

        <FileHint kind="environments" info={store.environmentsFile} settingName="OSB_EXPLORER_ENVIRONMENTS_FILE" />

        <div className="flex gap-4">
          <div className={cn("w-full shrink-0 space-y-0.5 md:w-64", editorOpen && "hidden md:block")}>
            {store.environments.length === 0 && !creating && (
              <p className="rounded-md border border-dashed p-4 text-center text-xs text-muted-foreground">
                No environments yet. Create one, or import a Postman environment export.
              </p>
            )}
            {store.environments.map((e) => {
              const active = store.activeEnvironment === e.name;
              const current = !creating && selectedName === e.name;
              return (
                <button
                  key={e.name}
                  className={cn(
                    "group relative flex h-10 w-full items-center gap-2 rounded-md px-2.5 text-left text-sm hover:bg-accent",
                    current && "bg-accent font-medium",
                  )}
                  onClick={() => { setCreating(false); setSelectedName(e.name); }}
                >
                  {current && <span className="absolute inset-y-2 left-0 w-0.5 rounded-full bg-primary" />}
                  <GlobeIcon className={cn("size-4 shrink-0", active ? "text-sky-500" : "text-muted-foreground")} />
                  <span className="min-w-0 flex-1 truncate">{e.name}</span>
                  {active && <CheckCircle2Icon className="size-3.5 shrink-0 text-sky-500" />}
                  <Badge variant="muted" className="tabular-nums text-[10px]">{e.values.filter((v) => v.enabled).length}</Badge>
                </button>
              );
            })}
          </div>

          <div className={cn("min-w-0 flex-1", !editorOpen && "hidden md:block")}>
            {creating ? (
              <EnvironmentEditor
                key="@new"
                onClose={() => setCreating(false)}
                onSaved={(name) => { setCreating(false); setSelectedName(name); }}
              />
            ) : selected ? (
              <EnvironmentEditor
                key={selected.name}
                edit={selected}
                onClose={() => setSelectedName(null)}
                onSaved={(name) => setSelectedName(name)}
                onDeleted={() => setSelectedName(null)}
              />
            ) : (
              <p className="rounded-md border border-dashed p-8 text-center text-sm text-muted-foreground">
                Select an environment to edit it, or create a new one.
              </p>
            )}
          </div>
        </div>
      </div>
    </main>
  );
}

/** Inline editor panel: name + value rows with per-row enable toggles, explicit
 *  Save/Revert with dirty tracking, and active/duplicate/delete in the header. */
function EnvironmentEditor({
  edit, onClose, onSaved, onDeleted,
}: {
  edit?: ExplorerEnvironment;
  onClose: () => void;
  onSaved: (name: string) => void;
  onDeleted?: () => void;
}) {
  const store = useStore();
  const [name, setName] = useState(edit?.name ?? "");
  const [rows, setRows] = useState<EnvironmentValue[]>(
    edit?.values.length ? edit.values.map((v) => ({ ...v })) : [{ key: "", value: "", enabled: true }],
  );
  const [busy, setBusy] = useState(false);

  const cleanRows = (r: EnvironmentValue[]) =>
    r.filter((row) => row.key.trim() !== "").map((row) => ({ ...row, key: row.key.trim() }));
  const dirty = edit
    ? name !== edit.name || JSON.stringify(cleanRows(rows)) !== JSON.stringify(edit.values)
    : name.trim() !== "" || cleanRows(rows).length > 0;
  const isActive = edit !== undefined && store.activeEnvironment === edit.name;

  const patch = (i: number, change: Partial<EnvironmentValue>) =>
    setRows((r) => r.map((row, j) => (j === i ? { ...row, ...change } : row)));

  const save = async () => {
    const trimmed = name.trim();
    if (trimmed === "") return toast.error("A name is required.");
    setBusy(true);
    try {
      const environment: ExplorerEnvironment = { name: trimmed, values: cleanRows(rows) };
      if (edit) {
        await explorerApi.updateEnvironment(edit.name, environment);
        if (isActive && trimmed !== edit.name) store.setActiveEnvironment(trimmed);
      } else {
        await explorerApi.createEnvironment(environment);
      }
      toast.success(`Saved '${trimmed}'`);
      await store.refreshEnvironments();
      onSaved(trimmed);
    } catch (e) {
      toast.error("Save failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const duplicate = async () => {
    if (!edit) return;
    try {
      const copy = await explorerApi.duplicateEnvironment(edit.name);
      await store.refreshEnvironments();
      onSaved(copy.name);
      toast.success(`Duplicated as '${copy.name}'`);
    } catch (e) {
      toast.error("Duplicate failed: " + (e as Error).message);
    }
  };

  const remove = () => {
    if (!edit) return;
    store.setDialog({
      type: "confirm",
      title: `Delete environment '${edit.name}'?`,
      description: "This removes it for everyone using this Explorer.",
      destructive: true,
      action: async () => {
        await explorerApi.deleteEnvironment(edit.name);
        if (isActive) store.setActiveEnvironment(null);
        await store.refreshEnvironments();
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
        <span className="min-w-0 flex-1 truncate text-sm font-semibold">
          {edit ? edit.name : "New environment"}
          {dirty && <span className="ml-1.5 align-middle text-[10px] font-normal text-amber-500">● unsaved</span>}
        </span>
        {edit && (
          <div className="flex items-center gap-1">
            <Button
              variant={isActive ? "outline" : "default"}
              size="sm"
              className="h-7 text-xs"
              onClick={() => store.setActiveEnvironment(isActive ? null : edit.name)}
            >
              {isActive ? "Deactivate" : "Set active"}
            </Button>
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
        <div className="space-y-1">
          <Label>Name</Label>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Card of Alice" className="max-w-sm" />
        </div>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Values</Label>
            <Button variant="outline" size="sm" onClick={() => setRows((r) => [...r, { key: "", value: "", enabled: true }])}>
              <PlusIcon /> Add row
            </Button>
          </div>
          <p className="text-[11px] text-muted-foreground">
            Referenced as {"{{key}}"}; values may contain dynamic variables like {"{{$guid}}"}.
            Disabled rows never resolve.
          </p>
          {rows.map((row, i) => (
            <div key={i} className={cn("flex items-center gap-2", !row.enabled && "opacity-50")}>
              <Switch checked={row.enabled} onCheckedChange={(v) => patch(i, { enabled: v })} aria-label="Enabled" />
              <Input
                value={row.key} placeholder="key"
                onChange={(e) => patch(i, { key: e.target.value })}
                className="font-mono text-xs"
              />
              <Input
                value={row.value} placeholder="value"
                onChange={(e) => patch(i, { value: e.target.value })}
                className="font-mono text-xs"
              />
              <Button variant="ghost" size="icon" className="size-7 shrink-0" onClick={() => setRows((r) => r.filter((_, j) => j !== i))}>
                <XIcon className="size-3.5" />
              </Button>
            </div>
          ))}
        </div>

        <div className="flex justify-end gap-2 border-t pt-3">
          {edit ? (
            <Button
              variant="outline"
              disabled={!dirty || busy}
              onClick={() => {
                setName(edit.name);
                setRows(edit.values.length ? edit.values.map((v) => ({ ...v })) : [{ key: "", value: "", enabled: true }]);
              }}
            >
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

/** Slim file-backing status line shared by both library pages. */
export function FileHint({
  kind, info, settingName,
}: {
  kind: string;
  info: { configured: boolean; writable: boolean; path: string | null } | null;
  settingName: string;
}) {
  return (
    <div className="flex items-center gap-2 rounded-md border bg-muted/40 px-2.5 py-1.5 text-[11px] text-muted-foreground">
      <FileJsonIcon className="size-3.5 shrink-0" />
      {info?.configured ? (
        info.writable ? (
          <p className="truncate">
            Persisted to {info.path ? <code className="font-mono">{info.path}</code> : "the configured file"} - edits write back; commit it to share with your team.
          </p>
        ) : (
          <p className="truncate">
            File is <span className="font-medium text-amber-600 dark:text-amber-400">read-only</span> - edits stay in memory until restart or reload.
          </p>
        )
      ) : (
        <p className="truncate">
          In-memory only - set <code className="font-mono">{settingName}</code> to persist and commit the {kind}.
        </p>
      )}
    </div>
  );
}
