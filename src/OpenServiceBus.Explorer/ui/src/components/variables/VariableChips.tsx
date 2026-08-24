import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { collectVariables } from "@/lib/variables";
import { cn } from "@/lib/utils";

/** Compact chips for every dynamic variable found across the given fields. Hover (or
 *  focus) explains what each resolves to - the same surface environments will later use
 *  to show the active environment's value. */
export function VariableChips({
  fields, className, env = null,
}: {
  fields: Record<string, string>;
  className?: string;
  /** Active environment's enabled values; null = no active environment. */
  env?: Record<string, string> | null;
}) {
  const tokens = collectVariables(fields, env);
  if (tokens.length === 0) return null;

  return (
    <div className={cn("flex flex-wrap items-center gap-1.5", className)}>
      {tokens.map((token) => (
        <Tooltip key={token.raw}>
          <TooltipTrigger asChild>
            <span
              tabIndex={0}
              className={cn(
                "cursor-help rounded-md border px-1.5 py-0.5 font-mono text-[11px] font-medium",
                token.valid
                  ? token.kind === "env"
                    ? "border-sky-500/40 bg-sky-500/10 text-sky-600 dark:text-sky-400"
                    : "border-emerald-500/40 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400"
                  : "border-amber-500/40 bg-amber-500/10 text-amber-600 dark:text-amber-400",
              )}
            >
              {token.raw}
            </span>
          </TooltipTrigger>
          <TooltipContent className="max-w-64">
            {token.kind === "env" ? (
              token.valid ? (
                <>
                  <p className="text-xs opacity-70">active environment value</p>
                  <p className="break-all font-mono font-medium">{token.description}</p>
                </>
              ) : (
                <p className="font-medium">{token.description}</p>
              )
            ) : (
              <p className="font-medium">{token.valid ? token.description : token.description}</p>
            )}
            <p className="mt-0.5 text-xs opacity-70">in {token.fields.join(", ")}</p>
          </TooltipContent>
        </Tooltip>
      ))}
    </div>
  );
}
