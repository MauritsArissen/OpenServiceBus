import { cn } from "@/lib/utils";

/**
 * The OpenServiceBus mark: a white nested diamond on the indigo→fuchsia brand tile.
 * Vector, so it stays crisp at any size and matches the favicon exactly.
 */
export function BrandMark({ className }: { className?: string }) {
  return (
    <span
      className={cn(
        "grid place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-fuchsia-500 shadow-sm",
        className,
      )}
    >
      <svg viewBox="0 0 24 24" fill="none" className="size-[62%]">
        <path d="M12 4.97 L19.03 12 L12 19.03 L4.97 12 Z" stroke="white" strokeWidth="1.4" strokeLinejoin="round" />
        <path d="M12 9.56 L14.44 12 L12 14.44 L9.56 12 Z" fill="white" />
      </svg>
    </span>
  );
}
