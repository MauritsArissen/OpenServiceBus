import { PencilIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { useEffect, useState } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { explorerApi, type RuleInfo } from "@/lib/api";
import { useStore } from "@/store";

export function RulesTab({ topic, sub }: { topic: string; sub: string }) {
  const store = useStore();
  const [rules, setRules] = useState<RuleInfo[] | null>(null);

  const load = async () => {
    try {
      setRules(await explorerApi.listRules(store.mgmt, topic, sub));
    } catch (e) {
      toast.error("Loading rules failed: " + (e as Error).message);
    }
  };

  useEffect(() => {
    void load();
    // reload when the dialog closes (a rule may have been added/edited)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [topic, sub, store.dialog]);

  const del = (name: string) =>
    store.setDialog({
      type: "confirm",
      title: `Delete rule '${name}'?`,
      description: "Messages matching only this rule will no longer be delivered to the subscription.",
      destructive: true,
      action: async () => {
        await explorerApi.deleteRule(store.mgmt, topic, sub, name);
        toast.success(`Deleted rule '${name}'`);
        await load();
      },
    });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-muted-foreground">
          A message is delivered if <em>any</em> rule matches. A subscription with no rules matches nothing.
        </p>
        <Button size="sm" onClick={() => store.setDialog({ type: "rule", topic, sub })}>
          <PlusIcon /> Add rule
        </Button>
      </div>

      {rules === null ? (
        <div className="text-sm text-muted-foreground">Loading…</div>
      ) : rules.length === 0 ? (
        <div className="rounded-xl border border-dashed p-8 text-center text-sm text-muted-foreground">
          This subscription has no rules. No messages will be delivered until you add one.
        </div>
      ) : (
        rules.map((r) => (
          <Card key={r.name}>
            <CardContent className="pt-5">
              <div className="mb-2 flex items-center gap-2">
                <span className="font-mono font-semibold">{r.name}</span>
                {r.name === "$Default" && <Badge variant="secondary">default</Badge>}
                <Badge variant="muted">{r.filterType ?? "sql"}</Badge>
                <div className="ml-auto flex gap-1">
                  <Button variant="ghost" size="icon-sm" onClick={() => store.setDialog({ type: "rule", topic, sub, edit: r })}>
                    <PencilIcon />
                  </Button>
                  <Button variant="ghost" size="icon-sm" className="text-destructive" onClick={() => del(r.name)}>
                    <Trash2Icon />
                  </Button>
                </div>
              </div>
              <RuleBody rule={r} />
            </CardContent>
          </Card>
        ))
      )}
    </div>
  );
}

function RuleBody({ rule }: { rule: RuleInfo }) {
  const t = (rule.filterType ?? "sql").toLowerCase();
  if (t === "sql") {
    return (
      <pre className="overflow-x-auto rounded-md bg-muted p-2.5 font-mono text-xs">
        {rule.sqlExpression || "(empty)"}
      </pre>
    );
  }
  if (t === "true") return <p className="text-sm text-muted-foreground">Always matches (catch-all).</p>;
  if (t === "false") return <p className="text-sm text-muted-foreground">Never matches.</p>;
  // correlation
  const pairs: [string, string | null | undefined][] = [
    ["MessageId", rule.messageId], ["CorrelationId", rule.correlationId], ["Subject", rule.subject],
    ["To", rule.to], ["ReplyTo", rule.replyTo], ["ReplyToSessionId", rule.replyToSessionId],
    ["SessionId", rule.sessionId], ["ContentType", rule.contentType],
  ];
  const set = pairs.filter(([, v]) => v);
  if (set.length === 0)
    return <p className="text-sm text-muted-foreground">(no constraints - equivalent to TrueFilter)</p>;
  return (
    <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1 text-xs">
      {set.map(([k, v]) => (
        <div key={k} className="contents">
          <dt className="text-muted-foreground">{k}</dt>
          <dd className="font-mono">{v}</dd>
        </div>
      ))}
    </dl>
  );
}
