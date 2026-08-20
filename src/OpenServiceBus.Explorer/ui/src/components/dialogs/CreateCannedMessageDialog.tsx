import { useRef, useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { explorerApi } from "@/lib/api";
import { useStore } from "@/store";
import { TextField } from "./FormControls";
import { SendTab, type SendTabRef } from "../SendTab";

export function CreateCannedMessageDialog({
  presetTopicOrQueue: presetTopic,
}: {
  presetTopicOrQueue?: string;
}) {
  const store = useStore();
  const [topicOrQueue, setTopic] = useState(
    presetTopic ?? store.topics[0]?.name ?? "*",
  );
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const sendTabRef = useRef<SendTabRef>(null);

  const create = async () => {
    if (topicOrQueue === "") return toast.error("Select a topic or queue.");
    if (name.trim() === "")
      return toast.error("Canned message name is required.");
    setBusy(true);
    try {
      const sendPayload = sendTabRef.current?.getSendPayload();
      await explorerApi.createCannedMessage(
        store.mgmt,
        topicOrQueue,
        name.trim(),
        sendPayload,
      );
      toast.success(`Created canned message '${topicOrQueue}/${name.trim()}'`);
      store.setDialog(null);
      await store.refresh();
      // store.select({ kind: "cannedMessage", name: topicOrQueue, sub: name.trim() });
    } catch (e) {
      toast.error("Create failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent className="max-w-[50vw]">
      <DialogHeader>
        <DialogTitle>Create canned message</DialogTitle>
        <DialogDescription>
          Preset message for the selected topic/queue.
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <div className="space-y-1">
          <Label>Topic or Queue</Label>
          <Select value={topicOrQueue} onValueChange={setTopic}>
            <SelectTrigger>
              <SelectValue placeholder="Select a topic/queue" />
            </SelectTrigger>
            <SelectContent>
              {[{ name: "*" }, ...store.topics, ...store.queues].map((t) => (
                <SelectItem key={t.name} value={t.name}>
                  {t.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <TextField
          label="Canned message name"
          value={name}
          onChange={setName}
          mono
          placeholder="sample-message"
        />
        <div>
          <SendTab
            ref={sendTabRef}
            kind="cannedMessage"
            sel={{
              name: topicOrQueue,
              kind: store.queues.some((x) => x.name === topicOrQueue)
                ? "queue"
                : "topic",
            }}
          />
        </div>
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>
          Cancel
        </Button>
        <Button onClick={() => void create()} disabled={busy}>
          Create canned message
        </Button>
      </DialogFooter>
    </DialogContent>
  );
}
