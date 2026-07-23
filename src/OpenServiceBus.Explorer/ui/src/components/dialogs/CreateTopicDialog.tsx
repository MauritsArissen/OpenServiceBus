import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { explorerApi } from "@/lib/api";
import { secondsToTimeSpan } from "@/lib/format";
import { useStore } from "@/store";
import { NumberField, TextField } from "./FormControls";

export function CreateTopicDialog() {
  const store = useStore();
  const [name, setName] = useState("");
  const [ttl, setTtl] = useState("");
  const [busy, setBusy] = useState(false);

  const create = async () => {
    if (name.trim() === "") return toast.error("Topic name is required.");
    setBusy(true);
    try {
      const options: Record<string, unknown> = {};
      const ttlN = parseInt(ttl, 10);
      if (ttlN > 0) options.defaultMessageTimeToLive = secondsToTimeSpan(ttlN);
      await explorerApi.createTopic(store.mgmt, name.trim(), options);
      toast.success(`Created topic '${name.trim()}'`);
      store.setDialog(null);
      await store.refresh();
      store.select({ kind: "topic", name: name.trim() });
    } catch (e) {
      toast.error("Create failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Create topic</DialogTitle>
      </DialogHeader>
      <div className="space-y-3">
        <TextField label="Topic name" value={name} onChange={setName} mono placeholder="events" />
        <NumberField label="Default TTL (seconds)" value={ttl} onChange={setTtl} placeholder="∞" min={1} />
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void create()} disabled={busy}>Create topic</Button>
      </DialogFooter>
    </DialogContent>
  );
}
