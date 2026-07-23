import {
  ArchiveXIcon, CheckIcon, ClockIcon, MoreVerticalIcon, RotateCcwIcon, TimerIcon, Trash2Icon, UndoIcon,
} from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { type MessageDto } from "@/lib/api";
import { explorerApi } from "@/lib/api";
import { formatTime } from "@/lib/format";
import { cn } from "@/lib/utils";
import { msgKey, useStore } from "@/store";

export function MessageCard({
  msg, target, locked, isDlq,
}: { msg: MessageDto; target: string; locked: boolean; isDlq: boolean }) {
  const store = useStore();
  const [open, setOpen] = useState(false);
  const key = msgKey(msg);
  const preview = (msg.body ?? "").replace(/\s+/g, " ").slice(0, 140);
  const propCount = Object.keys(msg.applicationProperties ?? {}).length;

  const act = async (label: string, fn: () => Promise<void>) => {
    try {
      await fn();
    } catch (e) {
      toast.error(`${label} failed: ${(e as Error).message}`);
    }
  };

  const token = msg.lockToken!;
  const complete = () =>
    act("Complete", async () => {
      await explorerApi.complete(store.conn, target, token);
      store.untrack(target, key);
      toast.success(isDlq ? "Deleted" : "Completed");
      await store.refresh();
    });
  const abandon = () =>
    act("Abandon", async () => {
      await explorerApi.abandon(store.conn, target, token);
      store.untrack(target, key);
      toast.success("Abandoned");
      await store.refresh();
    });
  const renew = () =>
    act("Renew", async () => {
      const r = await explorerApi.renew(store.conn, target, token);
      store.updateLockedUntil(target, key, r.renewedUntil);
      toast.success(`Renewed until ${formatTime(r.renewedUntil)}`);
    });
  const defer = () =>
    act("Defer", async () => {
      const r = await explorerApi.defer(store.conn, target, token);
      store.untrack(target, key);
      toast.success(`Deferred (seq=${r.sequenceNumber})`);
      await store.refresh();
    });
  const requeue = () =>
    act("Requeue", async () => {
      const r = await explorerApi.requeue(store.conn, target, token);
      store.untrack(target, key);
      toast.success(`Requeued to ${r.target}`);
      await store.refresh();
    });
  const deadletter = () => store.setDialog({ type: "deadletter", target, lockToken: token });
  const deleteDlq = () =>
    store.setDialog({
      type: "confirm",
      title: "Delete dead-lettered message?",
      description: "The message is completed off the dead-letter queue and permanently removed.",
      destructive: true,
      action: complete,
    });

  return (
    <div className="rounded-lg border bg-card text-sm shadow-sm">
      <div
        className="flex cursor-pointer items-center gap-2 px-3 py-2"
        onClick={() => setOpen((o) => !o)}
      >
        <span className="min-w-0 max-w-[220px] truncate font-mono text-xs" title={msg.messageId ?? ""}>
          {msg.messageId ?? "(no id)"}
        </span>
        {msg.sequenceNumber != null && <Badge variant="muted">seq {msg.sequenceNumber}</Badge>}
        {(msg.deliveryCount ?? 0) > 1 && <Badge variant="warning">×{msg.deliveryCount}</Badge>}
        <Badge variant={locked ? "warning" : "muted"}>{locked ? "locked" : "peek"}</Badge>
        {locked && msg.lockedUntil && (
          <Badge variant="outline">
            <ClockIcon className="size-2.5" /> {formatTime(msg.lockedUntil)}
          </Badge>
        )}
        <span className="min-w-0 flex-1 truncate text-xs text-muted-foreground">{preview}</span>
        {locked && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild onClick={(e) => e.stopPropagation()}>
              <Button variant="ghost" size="icon-sm">
                <MoreVerticalIcon />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" onClick={(e) => e.stopPropagation()}>
              {isDlq ? (
                <>
                  <DropdownMenuItem onClick={() => void requeue()}>
                    <RotateCcwIcon /> Requeue
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem variant="destructive" onClick={deleteDlq}>
                    <Trash2Icon /> Delete
                  </DropdownMenuItem>
                </>
              ) : (
                <>
                  <DropdownMenuItem onClick={() => void complete()}>
                    <CheckIcon /> Complete
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={() => void abandon()}>
                    <UndoIcon /> Abandon
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={() => void renew()}>
                    <TimerIcon /> Renew lock
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={() => void defer()}>
                    <ClockIcon /> Defer
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem variant="destructive" onClick={deadletter}>
                    <ArchiveXIcon /> Dead-letter
                  </DropdownMenuItem>
                </>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>

      {open && (
        <div className="space-y-3 border-t px-3 py-3">
          {(msg.deadLetterReason || msg.deadLetterSource || msg.deadLetterErrorDescription) && (
            <div className="rounded-md border border-destructive/30 bg-destructive/5 p-2.5 text-xs">
              {msg.deadLetterSource && (
                <div><span className="text-muted-foreground">Source&nbsp;&nbsp;</span>{msg.deadLetterSource}</div>
              )}
              {msg.deadLetterReason && (
                <div><span className="text-muted-foreground">Reason&nbsp;&nbsp;</span>{msg.deadLetterReason}</div>
              )}
              {msg.deadLetterErrorDescription && (
                <div><span className="text-muted-foreground">Detail&nbsp;&nbsp;</span>{msg.deadLetterErrorDescription}</div>
              )}
            </div>
          )}
          <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1 text-xs">
            <Meta k="CorrelationId" v={msg.correlationId} />
            <Meta k="Subject" v={msg.subject} />
            <Meta k="ContentType" v={msg.contentType} />
            <Meta k="Enqueued" v={formatTime(msg.enqueuedTime)} />
            <Meta k="Expires" v={formatTime(msg.expiresAt)} />
            <Meta k="DeliveryCount" v={msg.deliveryCount != null ? String(msg.deliveryCount) : null} />
          </dl>
          {propCount > 0 && (
            <div>
              <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                Application properties ({propCount})
              </div>
              <pre className="overflow-x-auto rounded-md bg-muted p-2.5 font-mono text-[11px]">
                {JSON.stringify(msg.applicationProperties, null, 2)}
              </pre>
            </div>
          )}
          <pre
            className={cn(
              "overflow-x-auto whitespace-pre-wrap break-all rounded-md bg-muted p-2.5 font-mono text-[11px]",
              !msg.body && "text-muted-foreground",
            )}
          >
            {msg.body || "(empty body)"}
          </pre>
        </div>
      )}
    </div>
  );
}

function Meta({ k, v }: { k: string; v: string | null | undefined }) {
  if (!v || v === "-") return null;
  return (
    <>
      <dt className="text-muted-foreground">{k}</dt>
      <dd className="font-mono">{v}</dd>
    </>
  );
}
