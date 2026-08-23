import { BracesIcon } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { VARIABLE_LEGEND } from "@/lib/variables";

/** "Which variables can I use?" - a small reference dialog, reachable from anywhere a
 *  body is composed. Self-contained (own Dialog) so it can sit inside other dialogs. */
export function VariableLegend() {
  const [open, setOpen] = useState(false);

  return (
    <>
      <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs text-muted-foreground" onClick={() => setOpen(true)}>
        <BracesIcon className="size-3.5" /> Variables
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-[85vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Dynamic variables</DialogTitle>
            <DialogDescription>
              Replaced with real values at send time, independently for every copy of a
              multi-count send - so they also work against a real Azure Service Bus.
              Usable in the body, MessageId, CorrelationId, Subject, ReplyTo, To,
              SessionId, PartitionKey and application property values.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-3">
            {VARIABLE_LEGEND.map((entry) => (
              <div key={entry.syntax} className="rounded-md border p-2.5">
                <code className="rounded-sm bg-emerald-500/10 px-1 py-0.5 font-mono text-xs font-semibold text-emerald-600 dark:text-emerald-400">
                  {entry.syntax}
                </code>
                <p className="mt-1 text-sm">{entry.description}</p>
                <p className="mt-0.5 break-all font-mono text-[11px] text-muted-foreground">{entry.example}</p>
              </div>
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
