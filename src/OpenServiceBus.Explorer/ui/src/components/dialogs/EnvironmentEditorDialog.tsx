import { PlusIcon, XIcon } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { explorerApi, type EnvironmentValue, type ExplorerEnvironment } from "@/lib/api";
import { useStore } from "@/store";
import { TextField } from "./FormControls";

/** Create/edit form for an environment: a name plus key/value rows, each individually
 *  enabled - the Postman model. Values may contain {{$...}} dynamic variables. */
export function EnvironmentEditorDialog({ edit }: { edit?: ExplorerEnvironment }) {
  const store = useStore();
  const [name, setName] = useState(edit?.name ?? "");
  const [rows, setRows] = useState<EnvironmentValue[]>(
    edit?.values.length ? edit.values.map((v) => ({ ...v })) : [{ key: "", value: "", enabled: true }],
  );
  const [busy, setBusy] = useState(false);

  const patch = (i: number, change: Partial<EnvironmentValue>) =>
    setRows((r) => r.map((row, j) => (j === i ? { ...row, ...change } : row)));

  const save = async () => {
    const trimmed = name.trim();
    if (trimmed === "") return toast.error("A name is required.");
    setBusy(true);
    try {
      const environment: ExplorerEnvironment = {
        name: trimmed,
        values: rows.filter((r) => r.key.trim() !== "").map((r) => ({ ...r, key: r.key.trim() })),
      };
      if (edit) {
        await explorerApi.updateEnvironment(edit.name, environment);
      } else {
        await explorerApi.createEnvironment(environment);
      }
      toast.success(`Saved environment '${trimmed}'`);
      store.setDialog(null);
      await store.refreshEnvironments();
    } catch (e) {
      toast.error("Save failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-xl">
      <DialogHeader>
        <DialogTitle>{edit ? `Edit '${edit.name}'` : "New environment"}</DialogTitle>
        <DialogDescription>
          A named set of values. In payloads, {"{{key}}"} becomes the value of the active
          environment; values may themselves contain dynamic variables like {"{{$guid}}"}.
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-4">
        <TextField label="Name" value={name} onChange={setName} placeholder="e.g. Card of Alice" />
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Values</Label>
            <Button variant="outline" size="sm" onClick={() => setRows((r) => [...r, { key: "", value: "", enabled: true }])}>
              <PlusIcon /> Add row
            </Button>
          </div>
          {rows.map((row, i) => (
            <div key={i} className="flex items-center gap-2">
              <Switch
                checked={row.enabled}
                onCheckedChange={(v) => patch(i, { enabled: v })}
                aria-label="Enabled"
              />
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
      </div>
      <DialogFooter className="mt-2">
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void save()} disabled={busy}>{busy ? "Saving…" : "Save"}</Button>
      </DialogFooter>
    </DialogContent>
  );
}
