import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi, type CannedMessage } from "@/lib/api";
import { useStore } from "@/store";
import { TextField } from "./FormControls";

export function SaveCannedDialog({ draft }: { draft: CannedMessage }) {
  const store = useStore();
  const [name, setName] = useState(draft.name);
  const [scope, setScope] = useState(draft.targetEntity && draft.targetEntity !== "*" ? "entity" : "any");
  const [overwrite, setOverwrite] = useState(false);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    const trimmed = name.trim();
    if (trimmed === "") return toast.error("A name is required.");
    setBusy(true);
    try {
      const message: CannedMessage = {
        ...draft,
        name: trimmed,
        targetEntity: scope === "entity" ? draft.targetEntity : "*",
      };
      if (overwrite) {
        await explorerApi.updateCanned(trimmed, message);
      } else {
        await explorerApi.createCanned(message);
      }
      toast.success(`Saved canned message '${trimmed}'`);
      store.setDialog(null);
      await store.refreshCanned();
    } catch (e) {
      const msg = (e as Error).message;
      if (!overwrite && msg.includes("already exists")) {
        setOverwrite(true);
        toast.warning(`'${trimmed}' already exists - save again to overwrite it.`);
      } else {
        toast.error("Save failed: " + msg);
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Save as canned message</DialogTitle>
        <DialogDescription>
          Stores the current Send form, dynamic variables intact, for one-click reuse.
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <TextField
          label="Name"
          value={name}
          onChange={(v) => {
            setName(v);
            setOverwrite(false);
          }}
          placeholder="e.g. order-created"
        />
        <div className="space-y-1">
          <Label>Available on</Label>
          <Select value={scope} onValueChange={setScope}>
            <SelectTrigger><SelectValue /></SelectTrigger>
            <SelectContent>
              {draft.targetEntity && draft.targetEntity !== "*" && (
                <SelectItem value="entity">Only {draft.targetEntity}</SelectItem>
              )}
              <SelectItem value="any">Any entity</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void save()} disabled={busy} variant={overwrite ? "destructive" : "default"}>
          {busy ? "Saving…" : overwrite ? "Overwrite" : "Save"}
        </Button>
      </DialogFooter>
    </DialogContent>
  );
}
