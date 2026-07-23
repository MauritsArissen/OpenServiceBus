import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { useStore } from "@/store";

export function ConfirmDialog({
  title, description, destructive, action,
}: {
  title: string;
  description: string;
  destructive?: boolean;
  action: () => Promise<void> | void;
}) {
  const store = useStore();
  const [busy, setBusy] = useState(false);

  const run = async () => {
    setBusy(true);
    try {
      await action();
      store.setDialog(null);
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent className="max-w-md">
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>{description}</DialogDescription>
      </DialogHeader>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button variant={destructive ? "destructive" : "default"} onClick={() => void run()} disabled={busy}>
          {destructive ? "Delete" : "Confirm"}
        </Button>
      </DialogFooter>
    </DialogContent>
  );
}
