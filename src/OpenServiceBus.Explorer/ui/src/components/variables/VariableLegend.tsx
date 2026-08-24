import { BracesIcon } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { LEGEND_GROUPS } from "@/lib/variables";

/** The variables guide: every built-in grouped by purpose, plus the environment form.
 *  Click any syntax to copy it into the clipboard, ready to paste into a body. */
export function VariableLegend() {
  const [open, setOpen] = useState(false);

  const copy = (syntax: string) => {
    void navigator.clipboard?.writeText(syntax).then(() => toast.success(`Copied ${syntax}`));
  };

  return (
    <>
      <Button variant="ghost" size="sm" className="h-8 gap-1 px-2 text-xs text-muted-foreground" onClick={() => setOpen(true)}>
        <BracesIcon className="size-3.5" /> Variables
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>Variables</DialogTitle>
            <DialogDescription>
              Replaced with real values at send time, independently per copy of a
              multi-count send - so they also work against a real Azure Service Bus.
              Usable in the body, every system property field and application property
              values. Hover a highlighted token in the composer to see what it resolves
              to; click a syntax below to copy it.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-5">
            {LEGEND_GROUPS.map((group) => (
              <div key={group.title}>
                <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-[0.08em] text-muted-foreground">
                  {group.title}
                </h3>
                <div className="divide-y rounded-md border">
                  {group.entries.map((entry) => (
                    <div key={entry.syntax} className="flex flex-col gap-1 p-2.5 sm:flex-row sm:items-start sm:gap-3">
                      <button
                        className={
                          "shrink-0 self-start whitespace-nowrap rounded-sm px-1.5 py-0.5 text-left font-mono text-xs font-semibold " +
                          (group.title === "Environment"
                            ? "bg-sky-500/10 text-sky-600 hover:bg-sky-500/20 dark:text-sky-400"
                            : "bg-emerald-500/10 text-emerald-600 hover:bg-emerald-500/20 dark:text-emerald-400")
                        }
                        onClick={() => copy(entry.syntax)}
                        title="Copy"
                      >
                        {entry.syntax}
                      </button>
                      <div className="min-w-0">
                        <p className="text-sm">{entry.description}</p>
                        <p className="mt-0.5 break-all font-mono text-[11px] text-muted-foreground">{entry.example}</p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
