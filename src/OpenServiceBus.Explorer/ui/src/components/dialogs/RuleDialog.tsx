import { memo, useState, type ReactNode } from "react";
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
  const [sqlAction, setSqlAction] = useState(edit?.sqlActionExpression ?? "");
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
      if (sqlAction.trim()) rule.sqlAction = sqlAction.trim();
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
      await explorerApi.putRule(store.conn, topic, sub, name.trim(), rule);
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
              A message matches when the expression evaluates to true. The full grammar is
              in the syntax reference below.
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

        <div className="space-y-1">
          <Label>Action (SQL, optional)</Label>
          <Textarea
            rows={2}
            value={sqlAction}
            onChange={(e) => setSqlAction(e.target.value)}
            placeholder="SET sys.Label = 'tagged'; SET counter = counter + 1; REMOVE debug"
            className="font-mono text-xs"
          />
          <p className="text-[11px] text-muted-foreground">
            SET/REMOVE statements applied to this subscription's copy when the rule matches.
          </p>
        </div>

        <SyntaxReference />
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={() => store.setDialog(null)}>Cancel</Button>
        <Button onClick={() => void save()} disabled={busy}>Save rule</Button>
      </DialogFooter>
    </DialogContent>
  );
}

function Syntax({ code, children }: { code: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[minmax(0,13rem)_1fr] items-baseline gap-x-3 gap-y-0.5">
      <code className="break-words font-mono text-[11px] text-foreground">{code}</code>
      <span className="text-muted-foreground">{children}</span>
    </div>
  );
}

function SyntaxGroup({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="space-y-1">
      <div className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground/70">{title}</div>
      {children}
    </div>
  );
}

/** Everything the broker's SQL grammar supports, mirroring docs/Topics-and-Subscriptions.
    Memoized: the dialog re-renders on every store update (polling, counts), and this
    subtree is fully static. */
const SyntaxReference = memo(function SyntaxReference() {
  return (
    <details className="rounded-md border bg-muted/30 text-xs [&[open]>summary]:border-b">
      <summary className="cursor-pointer select-none px-3 py-2 font-medium text-muted-foreground hover:text-foreground">
        SQL syntax reference - operators, functions &amp; actions
      </summary>
      <div className="max-h-64 space-y-3 overflow-y-auto p-3">
        <SyntaxGroup title="Properties">
          <Syntax code="region  /  user.region">A custom (application) property; bare names default to user scope.</Syntax>
          <Syntax code="sys.Subject">
            A system property: MessageId, CorrelationId, Subject (alias Label), To, ReplyTo,
            ReplyToSessionId, SessionId, ContentType, EnqueuedTimeUtc.
          </Syntax>
          <Syntax code={'[order-id]  /  "order id"'}>Quote names with special characters or reserved words; ]] or "" escapes the closer.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Comparison & logic">
          <Syntax code="=  !=  <>  <  <=  >  >=">Compare numbers, strings, booleans. A missing property is NULL and never matches.</Syntax>
          <Syntax code="AND  OR  NOT  ( ... )">Combine predicates; SQL three-valued NULL logic applies.</Syntax>
          <Syntax code="IS NULL  /  IS NOT NULL">Test whether a value is (not) NULL.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Arithmetic">
          <Syntax code="+  -  *  /  %">On numbers: priority + 1 &gt;= 5, total % 2 = 0. Division always yields a decimal.</Syntax>
          <Syntax code="-value">Unary minus: -offset &lt; 0.</Syntax>
          <Syntax code="'a' + 'b'">+ concatenates when either side is a string.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Pattern & sets">
          <Syntax code="LIKE 'ord%'">% matches any run of characters, _ exactly one. Also NOT LIKE. Pattern and escape may be any string expression, e.g. LIKE prefix + '%'.</Syntax>
          <Syntax code="LIKE '100!%' ESCAPE '!'">The escape character makes the next character literal - here a real percent sign.</Syntax>
          <Syntax code="IN ('eu', 'us')">Value is (NOT IN: is not) one of the listed values.</Syntax>
          <Syntax code="EXISTS(prop)">The property is present on the message, whatever its value. Also NOT EXISTS.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Functions">
          <Syntax code="newid()">A fresh random GUID each evaluation.</Syntax>
          <Syntax code="property(name)  /  p(name)">The value of the named property; the name may be bare, sys./user.-scoped, or any string expression. Names are case-insensitive.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Literals">
          <Syntax code="'text'  42  3.14  1.5E3  TRUE  FALSE  NULL">Strings use single quotes; double the quote ('it''s') to escape one. Scientific notation is a decimal.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Parameters">
          <Syntax code="priority >= @threshold">@name references a value supplied at rule creation via the SDK's SqlRuleFilter.Parameters / SqlRuleAction.Parameters (admin client only). Undefined parameters are rejected on save.</Syntax>
        </SyntaxGroup>
        <SyntaxGroup title="Actions (on match)">
          <Syntax code="SET prop = expr">Set or overwrite a property on the delivered copy; the value side uses the full grammar above.</Syntax>
          <Syntax code="REMOVE prop">Delete a custom property. Separate multiple statements with ;</Syntax>
          <Syntax code="SET sys.Label = 'tagged'">
            Writable sys properties: Label, CorrelationId, To, ReplyTo, ReplyToSessionId,
            ContentType. Clear one with SET sys.X = NULL.
          </Syntax>
        </SyntaxGroup>
        <p className="text-[10px] text-muted-foreground/70">
          Invalid expressions are rejected when the rule is saved. Filters that error at
          evaluation time (e.g. arithmetic on text) count as a non-match. Not supported:
          BETWEEN and parameterized filters.
        </p>
      </div>
    </details>
  );
});
