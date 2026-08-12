import {
  ArchiveXIcon, CheckIcon, ChevronsRightIcon, ClockIcon, DownloadIcon, FileDownIcon, MoreVerticalIcon,
  RotateCcwIcon, SendIcon, Trash2Icon, UndoIcon, XIcon,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { explorerApi, type BulkAction, type BulkResult, type MessageDto } from "@/lib/api";
import { type Selected } from "@/lib/format";
import { msgKey, useStore } from "@/store";
import { MessageCard } from "./MessageCard";

const exportShape = (m: MessageDto) => ({
  sequenceNumber: m.sequenceNumber,
  messageId: m.messageId,
  correlationId: m.correlationId,
  subject: m.subject,
  contentType: m.contentType,
  enqueuedTime: m.enqueuedTime,
  expiresAt: m.expiresAt,
  timeToLive: m.timeToLive,
  deliveryCount: m.deliveryCount,
  deadLetterReason: m.deadLetterReason,
  deadLetterErrorDescription: m.deadLetterErrorDescription,
  deadLetterSource: m.deadLetterSource,
  applicationProperties: m.applicationProperties,
  body: m.body,
});

export function ReceiveTab({ sel, target, isDlq }: { sel: Selected; target: string; isDlq?: boolean }) {
  const store = useStore();
  const d = store.descriptorFor(sel);
  const requiresSession = !isDlq && !!d?.requiresSession;

  const [mode, setMode] = useState<"peek" | "lock">("peek");
  const [count, setCount] = useState("1");
  const [sessionId, setSessionId] = useState("");
  const [busy, setBusy] = useState(false);
  const [selKeys, setSelKeys] = useState<Set<string>>(new Set());
  const anchorRef = useRef<string | null>(null);

  const n = Math.min(100, Math.max(1, parseInt(count, 10) || 1));
  const lockedMap = store.locked[target] ?? {};
  const lockedList = Object.values(lockedMap).reverse();
  const peekedList = (store.peeked[target] ?? []).filter(
    (m) => !(m.sequenceNumber != null && lockedMap[`seq:${m.sequenceNumber}`]),
  );
  const cursor = store.peekCursor[target];

  const visible = [
    ...lockedList.map((m) => ({ m: m as MessageDto, locked: true, key: msgKey(m) })),
    ...peekedList.map((m) => ({ m, locked: false, key: msgKey(m) })),
  ];

  useEffect(() => {
    const avail = new Set(visible.map((v) => v.key));
    setSelKeys((prev) => {
      const next = new Set([...prev].filter((k) => avail.has(k)));
      return next.size === prev.size ? prev : next;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [store.locked[target], store.peeked[target]]);

  const selectedItems = visible.filter((v) => selKeys.has(v.key));
  const selCount = selectedItems.length;
  const allSelected = visible.length > 0 && selCount === visible.length;
  const lockedTokens = selectedItems.filter((v) => v.locked).map((v) => v.m.lockToken!);
  const hasPeekedSelected = selectedItems.some((v) => !v.locked);
  const actDisabled = busy || selCount === 0 || hasPeekedSelected;

  const toggle = (key: string, shift: boolean) => {
    setSelKeys((prev) => {
      const next = new Set(prev);
      const keys = visible.map((v) => v.key);
      if (shift && anchorRef.current != null && keys.includes(anchorRef.current)) {
        const a = keys.indexOf(anchorRef.current);
        const b = keys.indexOf(key);
        const [lo, hi] = a < b ? [a, b] : [b, a];
        for (let i = lo; i <= hi; i++) next.add(keys[i]);
      } else {
        if (next.has(key)) next.delete(key);
        else next.add(key);
        anchorRef.current = key;
      }
      return next;
    });
  };

  const toggleAll = () => {
    anchorRef.current = null;
    setSelKeys(allSelected ? new Set() : new Set(visible.map((v) => v.key)));
  };

  const run = async (continueCursor: boolean) => {
    setBusy(true);
    try {
      if (mode === "peek") {
        const from = continueCursor ? (cursor ?? 0) : 0;
        const r = await explorerApi.peek(store.conn, target, n, from);
        store.setPeekedFor(target, r.messages, { append: continueCursor });
        if (r.count > 0) {
          const maxSeq = Math.max(...r.messages.map((m) => m.sequenceNumber ?? 0));
          store.setPeekCursor(target, maxSeq + 1);
          toast.success(
            continueCursor
              ? `Peeked ${r.count}/${n} from ${target} (seq >= ${from})`
              : `Peeked ${r.count}/${n} from ${target}`,
          );
        } else if (continueCursor) {
          toast.info(`End of ${target} - nothing at seq >= ${from}`);
        } else {
          store.setPeekCursor(target, null);
          toast.info(`No messages in ${target}`);
        }
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

  const applyBulkResult = (r: BulkResult, label: string) => {
    const gone = new Set(r.results.filter((x) => x.ok || x.lockLost).map((x) => x.lockToken));
    const keys = Object.keys(lockedMap).filter((k) => gone.has(lockedMap[k].lockToken ?? ""));
    store.untrackMany(target, keys);
    if (r.failed === 0) {
      toast.success(`${label}: ${r.succeeded}/${r.total} succeeded`);
    } else {
      const firstError = r.results.find((x) => !x.ok)?.error ?? "unknown error";
      toast.warning(`${label}: ${r.succeeded} succeeded, ${r.failed} failed (${firstError})`);
    }
    void store.refresh();
  };

  const bulkAct = async (action: BulkAction, label: string) => {
    setBusy(true);
    try {
      const r = await explorerApi.bulk(store.conn, target, action, lockedTokens);
      applyBulkResult(r, label);
    } catch (e) {
      toast.error(`${label} failed: ` + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const selSeqs = selectedItems
    .map((v) => v.m.sequenceNumber)
    .filter((s): s is number => s != null);
  const bulkResend = () => store.setDialog({ type: "resend", target, sequenceNumbers: selSeqs });

  const bulkDeadletter = () => store.setDialog({ type: "deadletter", target, lockTokens: lockedTokens });
  const bulkDelete = () =>
    store.setDialog({
      type: "confirm",
      title: `Delete ${lockedTokens.length} dead-lettered message${lockedTokens.length === 1 ? "" : "s"}?`,
      description: "The messages are completed off the dead-letter queue and permanently removed.",
      destructive: true,
      action: () => bulkAct("complete", "Delete"),
    });

  const exportMessages = () => {
    const items = (selCount > 0 ? selectedItems : visible).map((v) => exportShape(v.m));
    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
    const name = `${target.replace(/[^a-zA-Z0-9._-]+/g, "_")}-messages-${stamp}.json`;
    const blob = new Blob([JSON.stringify(items, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    a.click();
    URL.revokeObjectURL(url);
    toast.success(`Exported ${items.length} message${items.length === 1 ? "" : "s"} to ${name}`);
  };

  const clearBrowsed = () => {
    store.setPeekedFor(target, []);
    store.setPeekCursor(target, null);
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
          <Button onClick={() => void run(false)} disabled={busy}>
            <DownloadIcon /> {mode === "peek" ? `Peek ${n}` : `Peek & Lock ${n}`}
          </Button>
          {mode === "peek" && cursor != null && (
            <Button variant="outline" onClick={() => void run(true)} disabled={busy}>
              <ChevronsRightIcon /> Peek next {n}
            </Button>
          )}
          <p className="basis-full text-xs text-muted-foreground">
            {isDlq
              ? `Dead-letter sub-queue: ${target}`
              : mode === "peek"
                ? cursor != null
                  ? `Peek reads without locking. Peek next continues from seq ${cursor}; Peek restarts from the head.`
                  : "Peek reads without locking - messages stay available to other receivers."
                : "Peek & Lock takes a lock so you can complete / abandon / dead-letter each message."}
          </p>
        </CardContent>
      </Card>

      {visible.length === 0 ? (
        <div className="rounded-xl border border-dashed p-8 text-center text-sm text-muted-foreground">
          No messages here yet. Peek to look without locking, or switch to Peek &amp; Lock to act on messages.
        </div>
      ) : (
        <div className="space-y-2">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1 rounded-lg border bg-card px-3 py-1.5 text-sm shadow-sm">
            <Checkbox
              checked={allSelected}
              indeterminate={selCount > 0 && !allSelected}
              aria-label="Select all messages"
              className="-ml-1.5"
              onToggle={toggleAll}
            />
            <span className="text-xs text-muted-foreground">
              {selCount > 0
                ? `${selCount} of ${visible.length} selected`
                : `${visible.length} message${visible.length === 1 ? "" : "s"}`}
            </span>
            <div className="ml-auto flex items-center gap-1.5">
              {peekedList.length > 0 && (
                <Button variant="ghost" size="sm" onClick={clearBrowsed} disabled={busy}>
                  <XIcon /> Clear
                </Button>
              )}
              <Button variant="outline" size="sm" onClick={exportMessages} disabled={busy}>
                <FileDownIcon /> Export{selCount > 0 ? ` ${selCount}` : " all"}
              </Button>
              {selCount > 0 && (
                <>
                  <div className="hidden items-center gap-1.5 sm:flex">
                    {isDlq ? (
                      <>
                        <Button variant="outline" size="sm" disabled={busy || selSeqs.length === 0} onClick={bulkResend}>
                          <SendIcon /> Resend
                        </Button>
                        <Button variant="outline" size="sm" disabled={actDisabled} onClick={() => void bulkAct("requeue", "Requeue")}>
                          <RotateCcwIcon /> Requeue
                        </Button>
                        <Button variant="destructive" size="sm" disabled={actDisabled} onClick={bulkDelete}>
                          <Trash2Icon /> Delete
                        </Button>
                      </>
                    ) : (
                      <>
                        <Button variant="outline" size="sm" disabled={actDisabled} onClick={() => void bulkAct("complete", "Complete")}>
                          <CheckIcon /> Complete
                        </Button>
                        <Button variant="outline" size="sm" disabled={actDisabled} onClick={() => void bulkAct("abandon", "Abandon")}>
                          <UndoIcon /> Abandon
                        </Button>
                        <Button variant="outline" size="sm" disabled={actDisabled} onClick={() => void bulkAct("defer", "Defer")}>
                          <ClockIcon /> Defer
                        </Button>
                        <Button variant="destructive" size="sm" disabled={actDisabled} onClick={bulkDeadletter}>
                          <ArchiveXIcon /> Dead-letter
                        </Button>
                      </>
                    )}
                  </div>
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild className="sm:hidden">
                      <Button variant="outline" size="icon-sm" disabled={busy}>
                        <MoreVerticalIcon />
                      </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end">
                      {isDlq ? (
                        <>
                          <DropdownMenuItem disabled={busy || selSeqs.length === 0} onClick={bulkResend}>
                            <SendIcon /> Resend {selCount}
                          </DropdownMenuItem>
                          <DropdownMenuItem disabled={actDisabled} onClick={() => void bulkAct("requeue", "Requeue")}>
                            <RotateCcwIcon /> Requeue {selCount}
                          </DropdownMenuItem>
                          <DropdownMenuSeparator />
                          <DropdownMenuItem variant="destructive" disabled={actDisabled} onClick={bulkDelete}>
                            <Trash2Icon /> Delete {selCount}
                          </DropdownMenuItem>
                        </>
                      ) : (
                        <>
                          <DropdownMenuItem disabled={actDisabled} onClick={() => void bulkAct("complete", "Complete")}>
                            <CheckIcon /> Complete {selCount}
                          </DropdownMenuItem>
                          <DropdownMenuItem disabled={actDisabled} onClick={() => void bulkAct("abandon", "Abandon")}>
                            <UndoIcon /> Abandon {selCount}
                          </DropdownMenuItem>
                          <DropdownMenuItem disabled={actDisabled} onClick={() => void bulkAct("defer", "Defer")}>
                            <ClockIcon /> Defer {selCount}
                          </DropdownMenuItem>
                          <DropdownMenuSeparator />
                          <DropdownMenuItem variant="destructive" disabled={actDisabled} onClick={bulkDeadletter}>
                            <ArchiveXIcon /> Dead-letter {selCount}
                          </DropdownMenuItem>
                        </>
                      )}
                    </DropdownMenuContent>
                  </DropdownMenu>
                </>
              )}
            </div>
            {hasPeekedSelected && selCount > 0 && (
              <p className="basis-full text-xs text-muted-foreground">
                Peeked messages hold no lock - switch to Peek &amp; Lock to settle them.
                {isDlq ? " Export and Resend still work." : " Export still works."}
              </p>
            )}
          </div>
          {visible.map((v) => (
            <MessageCard
              key={(v.locked ? "" : "peek-") + v.key}
              msg={v.m}
              target={target}
              locked={v.locked}
              isDlq={!!isDlq}
              checked={selKeys.has(v.key)}
              onToggleSelect={(shiftKey) => toggle(v.key, shiftKey)}
            />
          ))}
        </div>
      )}
    </div>
  );
}
