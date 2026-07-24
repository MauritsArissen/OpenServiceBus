import { cn } from "@/lib/utils";

/**
 * The OpenServiceBus mark: the gradient "S" with its two node dots. Served from
 * /favicon.svg (public/) so it stays crisp at any size and matches the favicon and
 * app icons exactly. Transparent background, so it reads on both light and dark themes.
 */
export function BrandMark({ className }: { className?: string }) {
  return (
    <img src="/favicon.svg" alt="OpenServiceBus" className={cn("object-contain", className)} />
  );
}
