import { InboxIcon, RadioIcon, WaypointsIcon, type LucideIcon } from "lucide-react";
import type { EntityKind } from "@/lib/format";

export const KIND_ICON: Record<EntityKind, LucideIcon> = {
  queue: InboxIcon,
  topic: RadioIcon,
  subscription: WaypointsIcon,
};

/** Soft tinted tile classes per entity kind - used for the header icon tile. */
export const KIND_TILE: Record<EntityKind, string> = {
  queue: "bg-indigo-500/10 text-indigo-600 dark:text-indigo-300",
  topic: "bg-violet-500/10 text-violet-600 dark:text-violet-300",
  subscription: "bg-sky-500/10 text-sky-600 dark:text-sky-300",
};
