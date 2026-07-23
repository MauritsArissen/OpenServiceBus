import * as React from "react";

// Lightweight collapsible built on <details>/<summary> semantics with controlled state.
type CollapsibleProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  trigger: React.ReactNode;
  children: React.ReactNode;
  className?: string;
};

export function Collapsible({ open, onOpenChange, trigger, children, className }: CollapsibleProps) {
  return (
    <div className={className}>
      <button
        type="button"
        onClick={() => onOpenChange(!open)}
        className="flex w-full items-center gap-1.5 text-left cursor-pointer"
        aria-expanded={open}
      >
        {trigger}
      </button>
      {open && children}
    </div>
  );
}
