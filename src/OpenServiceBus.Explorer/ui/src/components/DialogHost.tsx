import { Dialog } from "@/components/ui/dialog";
import { useStore } from "@/store";
import { ConfirmDialog } from "./dialogs/ConfirmDialog";
import { CreateQueueDialog } from "./dialogs/CreateQueueDialog";
import { CreateSubscriptionDialog } from "./dialogs/CreateSubscriptionDialog";
import { CreateTopicDialog } from "./dialogs/CreateTopicDialog";
import { DeadLetterDialog } from "./dialogs/DeadLetterDialog";
import { ResendDialog } from "./dialogs/ResendDialog";
import { RuleDialog } from "./dialogs/RuleDialog";

export function DialogHost() {
  const { dialog, setDialog } = useStore();

  return (
    <Dialog open={dialog !== null} onOpenChange={(o) => !o && setDialog(null)}>
      {dialog?.type === "createQueue" && <CreateQueueDialog />}
      {dialog?.type === "createTopic" && <CreateTopicDialog />}
      {dialog?.type === "createSubscription" && <CreateSubscriptionDialog presetTopic={dialog.topic} />}
      {dialog?.type === "rule" && <RuleDialog topic={dialog.topic} sub={dialog.sub} edit={dialog.edit} />}
      {dialog?.type === "deadletter" && <DeadLetterDialog target={dialog.target} lockTokens={dialog.lockTokens} />}
      {dialog?.type === "resend" && <ResendDialog target={dialog.target} sequenceNumbers={dialog.sequenceNumbers} />}
      {dialog?.type === "confirm" && (
        <ConfirmDialog
          title={dialog.title}
          description={dialog.description}
          destructive={dialog.destructive}
          action={dialog.action}
        />
      )}
    </Dialog>
  );
}
