import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { explorerApi, type RuleInfo } from "@/lib/api";
import { useStore } from "@/store";
import { TextField } from "./FormControls";

export function RuleDialog({ topic, sub, edit }: { topic: string; sub: string; edit?: RuleInfo }) {
  const store = useStore();
  const editing = !!edit;
  const [name, setName] = useState(edit?.name ?? "");
  const [filterType, setFilterType] = useState((edit?.filterType ?? "sql").toLowerCase());
  const [sql, setSql] = useState(edit?.sqlExpression ?? "");
  const [rcMessageId, setRcMessageId] = useState(edit?.messageId ?? "");
  const [rcCorrelationId, setRcCorrelationId] = useState(edit?.correlationId ?? "");
  const [rcSubject, setRcSubject] = useState(edit?.subject ?? "");
  const [rcSessionId, setRcSessionId] = useState(edit?.sessionId ?? "");
  const [rcContentType, setRcContentType] = useState(edit?.contentType ?? "");
  const [rcReplyTo, setRcReplyTo] = useState(edit?.replyTo ?? "");
  const [busy, setBusy] = useState(false);

  const save = async () => {
    if (name.trim() === "") return toast.error("Rule name is required.");
    setBusy(true);
    try {
      const rule: Record<string, unknown> = { filterType };
      if (filterType === "sql") {
        rule.sqlExpression = sql;
      } else if (filterType === "correlation") {
        const put = (k: string, v: string) => { if (v.trim()) rule[k] = v.trim(); };
        put("messageId", rcMessageId);
        put("correlationId", rcCorrelationId);
        put("subject", rcSubject);
        put("sessionId", rcSessionId);
        put("contentType", rcContentType);
        put("replyTo", rcReplyTo);
      }
      await explorerApi.putRule(store.mgmt, topic, sub, name.trim(), rule);
      toast.success(`${editing ? "Updated" : "Added"} rule '${name.trim()}'`);
      store.setDialog(null); // RulesTab reloads on dialog change
    } catch (e) {
      toast.error("Save failed: " + (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DialogContent className="max-w-2xl">
      <DialogHeader>
        <DialogTitle>{editing ? "Edit rule" : "Add rule"}</DialogTitle>
        <DialogDescription>A message matches the subscription if any rule's filter returns true.</DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <TextField label="Rule name" value={name} onChange={setName} mono disabled={editing} />
          <div className="space-y-1">
            <Label>Filter type</Label>
            <Select value={filterType} onValueChange={setFilterType}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="sql">SQL</SelectItem>
                <SelectItem value="correlation">Correlation</SelectItem>
                <SelectItem value="true">True (catch-all)</SelectItem>
                <SelectItem value="false">False (drop all)</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        {filterType === "sql" && (
          <div className="space-y-1">
            <Label>SQL expression</Label>
            <Textarea
              rows={4}
              value={sql}
              onChange={(e) => setSql(e.target.value)}
              placeholder="region = 'eu' AND priority >= 5"
              className="font-mono text-xs"
            />
            <p className="text-[11px] text-muted-foreground">
              Supports = != &lt; &gt; &lt;= &gt;=, AND/OR/NOT, IS [NOT] NULL, LIKE (% and _), [NOT] IN (…),
              [NOT] EXISTS(prop). Reference properties bare, or with sys. / user. prefixes.
            </p>
          </div>
        )}

        {filterType === "correlation" && (
          <div className="grid grid-cols-2 gap-3">
            <TextField label="MessageId" value={rcMessageId} onChange={setRcMessageId} mono />
            <TextField label="CorrelationId" value={rcCorrelationId} onChange={setRcCorrelationId} mono />
            <TextField label="Subject" value={rcSubject} onChange={setRcSubject} mono />
            <TextField label="SessionId" value={rcSessionId} onChange={setRcSessionId} mono />
            <TextField label="ContentType" value={rcContentType} onChange={setRcContentType} mono />
            <TextField label="ReplyTo" value={rcReplyTo} onChange={setRcReplyTo} mono />
            <p className="col-span-2 text-[11px] text-muted-foreground">Empty fields are wildcards.</p>
          </div>
        )}

        {(filterType === "true" || filterType === "false") && (
          <div className="rounded-md border bg-muted/40 p-3 text-sm text-muted-foreground">
            {filterType === "true"
              ? "A TrueFilter matches every message published to the topic."
              : "A FalseFilter matches nothing - useful as a placeholder before adding real rules."}
          </div>
        )}
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void save()} disabled={busy}>Save rule</Button>
      </DialogFooter>
    </DialogContent>
  );
}
