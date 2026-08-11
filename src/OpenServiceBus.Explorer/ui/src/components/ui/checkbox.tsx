import { CheckIcon, MinusIcon } from "lucide-react";
import { cn } from "@/lib/utils";

export function Checkbox({
  checked,
  indeterminate = false,
  disabled = false,
  onToggle,
  className,
  "aria-label": ariaLabel,
}: {
  checked: boolean;
  indeterminate?: boolean;
  disabled?: boolean;
  onToggle: (e: React.MouseEvent) => void;
  className?: string;
  "aria-label"?: string;
}) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={indeterminate ? "mixed" : checked}
      aria-label={ariaLabel}
      disabled={disabled}
      onMouseDown={(e) => e.preventDefault()}
      onClick={(e) => {
        e.stopPropagation();
        onToggle(e);
      }}
      className={cn(
        "flex size-8 shrink-0 cursor-pointer items-center justify-center outline-none sm:size-6",
        disabled && "cursor-not-allowed",
        className,
      )}
    >
      <span
        className={cn(
          "flex size-4 items-center justify-center rounded-[4px] border border-input bg-background text-primary-foreground shadow-xs transition-colors",
          (checked || indeterminate) && "border-primary bg-primary",
          disabled && "opacity-40",
        )}
      >
        {indeterminate ? <MinusIcon className="size-3" /> : checked ? <CheckIcon className="size-3" /> : null}
      </span>
    </button>
  );
}
