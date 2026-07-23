import { DownloadIcon } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi, type MessageDto } from "@/lib/api";
import { type Selected } from "@/lib/format";
import { msgKey, useStore } from "@/store";
import { MessageCard } from "./MessageCard";

export function ReceiveTab({ sel, target, isDlq }: { sel: Selected; target: string; isDlq?: boolean }) {
  const store = useStore();
  const d = store.descriptorFor(sel);
  const requiresSession = !isDlq && !!d?.requiresSession;

  const [mode, setMode] = useState<"peek" | "lock">("peek");
  const [count, setCount] = useState("1");
  const [sessionId, setSessionId] = useState("");
  const [busy, setBusy] = useState(false);

  const n = Math.min(100, Math.max(1, parseInt(count, 10) || 1));
  const lockedMap = store.locked[target] ?? {};
  const lockedList = Object.values(lockedMap).reverse();
  const peekedList = (store.peeked[target] ?? []).filter(
    (m) => !(m.sequenceNumber != null && lockedMap[`seq:${m.sequenceNumber}`]),
  );

  const run = async () => {
    setBusy(true);
    try {
      if (mode === "peek") {
        const r = await explorerApi.peek(store.conn, target, n);
        store.setPeekedFor(target, r.messages);
        toast[r.count > 0 ? "success" : "info"](
          r.count > 0 ? `Peeked ${r.count}/${n} from ${target}` : `No messages in ${target}`,
        );
      } else {
        if (requiresSession && sessionId.trim() === "") {
          toast.error("Session ID is required on session-enabled entities.");
          return;
        }
        let got = 0;
        for (let i = 0; i < n; i++) {
          const r = await explorerApi.receive(store.conn, target, requiresSession ? sessionId.trim() : null);
          if (!("lockToken" in r) || !r.received) break;
          store.trackLocked(target, r as MessageDto);
          got++;
        }
        toast[got > 0 ? "success" : "info"](
          got > 0 ? `Locked ${got}/${n} from ${target}` : `No messages in ${target}`,
        );
        await store.refresh();
      }
    } catch (e) {
      toast.error("Receive failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="flex flex-wrap items-end gap-3 pt-5">
          <div className="space-y-1">
            <Label>Mode</Label>
            <Select value={mode} onValueChange={(v) => setMode(v as "peek" | "lock")}>
              <SelectTrigger className="w-36"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="peek">Peek</SelectItem>
                <SelectItem value="lock">Peek &amp; Lock</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1">
            <Label>Count</Label>
            <Input type="number" min={1} max={100} value={count} onChange={(e) => setCount(e.target.value)} className="w-24" />
          </div>
          {requiresSession && mode === "lock" && (
            <div className="space-y-1">
              <Label>Session</Label>
              <Input
                value={sessionId}
                onChange={(e) => setSessionId(e.target.value)}
                placeholder="session-id (required)"
                className="w-52 font-mono text-xs"
              />
            </div>
          )}
          <Button onClick={() => void run()} disabled={busy}>
            <DownloadIcon /> {mode === "peek" ? `Peek ${n}` : `Peek & Lock ${n}`}
          </Button>
          <p className="basis-full text-xs text-muted-foreground">
            {isDlq
              ? `Dead-letter sub-queue: ${target}`
              : mode === "peek"
                ? "Peek reads without locking - messages stay available to other receivers."
                : "Peek & Lock takes a lock so you can complete / abandon / dead-letter each message."}
          </p>
        </CardContent>
      </Card>

      {lockedList.length === 0 && peekedList.length === 0 ? (
        <div className="rounded-xl border border-dashed p-8 text-center text-sm text-muted-foreground">
          No messages here yet. Peek to look without locking, or switch to Peek &amp; Lock to act on messages.
        </div>
      ) : (
        <div className="space-y-2">
          {lockedList.map((m) => (
            <MessageCard key={msgKey(m)} msg={m} target={target} locked isDlq={!!isDlq} />
          ))}
          {peekedList.map((m) => (
            <MessageCard key={"peek-" + msgKey(m)} msg={m} target={target} locked={false} isDlq={!!isDlq} />
          ))}
        </div>
      )}
    </div>
  );
}
