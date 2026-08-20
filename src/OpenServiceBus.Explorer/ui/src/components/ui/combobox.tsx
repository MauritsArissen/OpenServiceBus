import * as PopoverPrimitive from "@radix-ui/react-popover";
import { CheckIcon, ChevronDownIcon } from "lucide-react";
import * as React from "react";
import { cn } from "@/lib/utils";

type ComboboxContextValue = {
  value: string;
  onValueChange: (value: string) => void;
  options: string[];
  filtered: string[];
  open: boolean;
  setOpen: (open: boolean) => void;
  activeIndex: number;
  setActiveIndex: React.Dispatch<React.SetStateAction<number>>;
  inputRef: React.RefObject<HTMLInputElement | null>;
  listId: string;
  container?: HTMLElement | null;
};

const ComboboxContext = React.createContext<ComboboxContextValue | null>(null);

function useComboboxContext(component: string) {
  const ctx = React.useContext(ComboboxContext);
  if (!ctx)
    throw new Error(`<${component} /> must be used within <Combobox />`);
  return ctx;
}

type ComboboxProps = {
  options: string[];
  value: string;
  onValueChange: (value: string) => void;
  children: React.ReactNode;
};

function Combobox({ options, value, onValueChange, children }: ComboboxProps) {
  const [open, setOpen] = React.useState(false);
  const [activeIndex, setActiveIndex] = React.useState(-1);
  const inputRef = React.useRef<HTMLInputElement>(null);
  const listId = React.useId();

  const filtered = React.useMemo(() => {
    const q = value.trim().toLowerCase();
    if (!q) return options;
    return options.filter((o) => o.toLowerCase().includes(q));
  }, [options, value]);

  return (
    <ComboboxContext.Provider
      value={{
        value,
        onValueChange,
        options,
        filtered,
        open: open && filtered.length > 0,
        setOpen,
        activeIndex,
        setActiveIndex,
        inputRef,
        listId,
      }}
    >
      <PopoverPrimitive.Root
        modal
        open={open && filtered.length > 0}
        onOpenChange={setOpen}
      >
        {children}
      </PopoverPrimitive.Root>
    </ComboboxContext.Provider>
  );
}

function ComboboxInput({
  className,
  onKeyDown,
  ...props
}: React.ComponentProps<"input">) {
  const {
    value,
    onValueChange,
    filtered,
    setOpen,
    activeIndex,
    setActiveIndex,
    inputRef,
    listId,
    open,
  } = useComboboxContext("ComboboxInput");

  function commit(val: string) {
    onValueChange(val);
    setOpen(false);
    setActiveIndex(-1);
    inputRef.current?.focus();
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    onKeyDown?.(e);
    if (e.defaultPrevented) return;

    if (!open && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
      setOpen(true);
      return;
    }
    if (!open) return;

    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActiveIndex((i) => Math.min(i + 1, filtered.length - 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActiveIndex((i) => Math.max(i - 1, 0));
    } else if (e.key === "Enter") {
      if (activeIndex >= 0 && filtered[activeIndex] != null) {
        e.preventDefault();
        commit(filtered[activeIndex]);
      } else {
        setOpen(false);
      }
    } else if (e.key === "Escape") {
      setOpen(false);
      setActiveIndex(-1);
    }
  }

  return (
    <PopoverPrimitive.Anchor asChild>
      <div className="relative">
        <input
          ref={inputRef}
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          className={cn(
            "flex h-9 w-full items-center rounded-md border border-input bg-transparent px-3 py-2 pr-8 text-sm shadow-sm outline-none focus:ring-2 focus:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
            className,
          )}
          value={value}
          onChange={(e) => {
            onValueChange(e.target.value);
            setOpen(true);
            setActiveIndex(-1);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={handleKeyDown}
          {...props}
        />
        <button
          type="button"
          tabIndex={-1}
          aria-label={open ? "Close suggestions" : "Open suggestions"}
          onMouseDown={(e) => {
            // prevent input blur; toggle explicitly instead of relying on focus
            e.preventDefault();
            setOpen(!open);
            inputRef.current?.focus();
          }}
          className="absolute right-2 top-1/2 -translate-y-1/2 flex size-4 items-center justify-center text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50"
        >
          <ChevronDownIcon
            className={cn("size-4 transition-transform", open && "rotate-180")}
          />
        </button>
      </div>
    </PopoverPrimitive.Anchor>
  );
}

function ComboboxContent({
  className,
  emptyText = "No matches — press Enter to use your text",
  ...props
}: React.ComponentProps<typeof PopoverPrimitive.Content> & {
  emptyText?: string;
}) {
  const { filtered, listId, container } = useComboboxContext("ComboboxContent");

  return (
    <PopoverPrimitive.Portal container={container ?? undefined}>
      <PopoverPrimitive.Content
        id={listId}
        role="listbox"
        align="start"
        sideOffset={4}
        onOpenAutoFocus={(e) => e.preventDefault()}
        className={cn(
          "relative z-50 max-h-60 w-(--radix-popover-trigger-width) overflow-auto rounded-md border bg-popover p-1 text-popover-foreground shadow-md data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95",
          className,
        )}
        {...props}
      >
        {filtered.length === 0 ? (
          <div className="px-2 py-1.5 text-sm text-muted-foreground">
            {emptyText}
          </div>
        ) : (
          filtered.map((option, index) => (
            <ComboboxItem key={option} option={option} index={index} />
          ))
        )}
      </PopoverPrimitive.Content>
    </PopoverPrimitive.Portal>
  );
}

function ComboboxItem({ option, index }: { option: string; index: number }) {
  const {
    value,
    onValueChange,
    activeIndex,
    setActiveIndex,
    setOpen,
    inputRef,
  } = useComboboxContext("ComboboxItem");
  const selected = option === value;
  const active = index === activeIndex;

  return (
    <div
      role="option"
      aria-selected={active}
      data-disabled={undefined}
      onMouseEnter={() => setActiveIndex(index)}
      onMouseDown={(e) => {
        e.preventDefault();
        onValueChange(option);
        setOpen(false);
        setActiveIndex(-1);
        inputRef.current?.focus();
      }}
      className={cn(
        "relative flex w-full cursor-pointer select-none items-center rounded-sm py-1.5 pl-2 pr-8 text-sm outline-none",
        active && "bg-accent text-accent-foreground",
      )}
    >
      <span className="absolute right-2 flex size-3.5 items-center justify-center">
        {selected && <CheckIcon className="size-4" />}
      </span>
      {option}
    </div>
  );
}

export { Combobox, ComboboxInput, ComboboxContent, ComboboxItem };
