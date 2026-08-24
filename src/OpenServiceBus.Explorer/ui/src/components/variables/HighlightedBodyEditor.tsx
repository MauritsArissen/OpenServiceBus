import { useRef } from "react";
import { Textarea } from "@/components/ui/textarea";
import { tokenizeVariables } from "@/lib/variables";
import { cn } from "@/lib/utils";

/** Body editor that paints {{$...}} tokens - green when the variable is valid, amber
 *  when unknown or malformed - via a metric-matched backdrop behind a transparent-text
 *  textarea. The textarea stays fully interactive; hover details live in the chips row
 *  and the legend, which also work on touch screens. */
export function HighlightedBodyEditor({
  value, onChange, rows = 7, placeholder, env = null,
}: {
  value: string;
  onChange: (v: string) => void;
  rows?: number;
  placeholder?: string;
  /** Active environment's enabled values; null = no active environment. */
  env?: Record<string, string> | null;
}) {
  const backdrop = useRef<HTMLDivElement>(null);

  const parts: React.ReactNode[] = [];
  let cursor = 0;
  for (const token of tokenizeVariables(value, env)) {
    if (token.start > cursor) parts.push(value.slice(cursor, token.start));
    parts.push(
      <span
        key={token.start}
        className={cn(
          "rounded-sm font-semibold",
          token.valid
            ? token.kind === "env"
              ? "bg-sky-500/15 text-sky-600 dark:text-sky-400"
              : "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
            : "bg-amber-500/15 text-amber-600 underline decoration-wavy decoration-amber-500/70 dark:text-amber-400",
        )}
      >
        {token.raw}
      </span>,
    );
    cursor = token.end;
  }
  if (cursor < value.length) parts.push(value.slice(cursor));

  return (
    <div className="relative">
      <div
        ref={backdrop}
        aria-hidden
        className="pointer-events-none absolute inset-0 overflow-hidden whitespace-pre-wrap break-words rounded-md border border-transparent px-3 py-2 font-mono text-xs"
      >
        {parts}
        {"\n"}
      </div>
      <Textarea
        rows={rows}
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        onScroll={(e) => {
          if (backdrop.current) backdrop.current.scrollTop = e.currentTarget.scrollTop;
        }}
        className="relative bg-transparent font-mono text-xs text-transparent caret-foreground selection:bg-primary/25 placeholder:text-muted-foreground"
      />
    </div>
  );
}
