import { useRef } from "react";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { tokenizeVariables } from "@/lib/variables";
import { cn } from "@/lib/utils";

/** Body editor that paints {{$...}} and {{env}} tokens in place and explains them ON
 *  HOVER, directly in the text: the colored layer sits above a transparent-text
 *  textarea, inert except for the token spans, which carry tooltips. Clicking a token
 *  drops the caret at its end (double-click selects it), so editing stays seamless. */
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
  const overlay = useRef<HTMLDivElement>(null);
  const textarea = useRef<HTMLTextAreaElement>(null);

  const focusAt = (start: number, end: number) => {
    const el = textarea.current;
    if (!el) return;
    el.focus();
    el.setSelectionRange(start, end);
  };

  const parts: React.ReactNode[] = [];
  let cursor = 0;
  for (const token of tokenizeVariables(value, env)) {
    if (token.start > cursor) parts.push(value.slice(cursor, token.start));
    parts.push(
      <Tooltip key={token.start}>
        <TooltipTrigger asChild>
          <span
            className={cn(
              "pointer-events-auto cursor-text rounded-sm font-semibold",
              token.valid
                ? token.kind === "env"
                  ? "bg-sky-500/15 text-sky-600 dark:text-sky-400"
                  : "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
                : "bg-amber-500/15 text-amber-600 underline decoration-wavy decoration-amber-500/70 dark:text-amber-400",
            )}
            onMouseDown={(e) => {
              e.preventDefault();
              focusAt(token.end, token.end);
            }}
            onDoubleClick={(e) => {
              e.preventDefault();
              focusAt(token.start, token.end);
            }}
          >
            {token.raw}
          </span>
        </TooltipTrigger>
        <TooltipContent className="max-w-72">
          {token.kind === "env" && token.valid ? (
            <>
              <p className="text-xs opacity-70">active environment value</p>
              <p className="break-all font-mono font-medium">{token.description}</p>
            </>
          ) : (
            <p className={cn("font-medium", !token.valid && "text-amber-100")}>{token.description}</p>
          )}
        </TooltipContent>
      </Tooltip>,
    );
    cursor = token.end;
  }
  if (cursor < value.length) parts.push(value.slice(cursor));

  return (
    <div className="relative">
      <Textarea
        ref={textarea}
        rows={rows}
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        onScroll={(e) => {
          if (overlay.current) overlay.current.scrollTop = e.currentTarget.scrollTop;
        }}
        className="font-mono text-xs text-transparent caret-foreground selection:bg-primary/25 placeholder:text-muted-foreground"
      />
      <div
        ref={overlay}
        aria-hidden
        className="pointer-events-none absolute inset-0 overflow-hidden whitespace-pre-wrap break-words rounded-md border border-transparent px-3 py-2 font-mono text-xs"
      >
        {parts}
        {"\n"}
      </div>
    </div>
  );
}
