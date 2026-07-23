import { Toaster } from "sonner";
import { DialogHost } from "@/components/DialogHost";
import { EntityView } from "@/components/EntityView";
import { Sidebar } from "@/components/Sidebar";
import { Topbar } from "@/components/Topbar";
import { TooltipProvider } from "@/components/ui/tooltip";
import { StoreProvider } from "@/store";

export default function App() {
  return (
    <StoreProvider>
      <TooltipProvider delayDuration={200}>
      <div
        className="grid h-full"
        style={{
          gridTemplateColumns: "300px 1fr",
          gridTemplateRows: "3.25rem 1fr",
          gridTemplateAreas: '"topbar topbar" "sidebar main"',
        }}
      >
        <Topbar />
        <Sidebar />
        <EntityView />
      </div>
      <DialogHost />
      <Toaster richColors position="bottom-right" closeButton />
      </TooltipProvider>
    </StoreProvider>
  );
}
