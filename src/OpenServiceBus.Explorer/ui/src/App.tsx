import { useState } from "react";
import { Toaster } from "sonner";
import { DialogHost } from "@/components/DialogHost";
import { EntityView } from "@/components/EntityView";
import { Sidebar } from "@/components/Sidebar";
import { Topbar } from "@/components/Topbar";
import { TooltipProvider } from "@/components/ui/tooltip";
import { UpdateBanner } from "@/components/UpdateBanner";
import { StoreProvider } from "@/store";

export default function App() {
  // On phones the sidebar becomes an off-canvas drawer; on md+ it's a static column
  // and this flag is irrelevant (the sidebar is always shown there).
  const [sidebarOpen, setSidebarOpen] = useState(false);

  return (
    <StoreProvider>
      <TooltipProvider delayDuration={200}>
        <div className="flex h-full flex-col">
          <Topbar onMenuClick={() => setSidebarOpen((v) => !v)} />
          <div className="relative flex min-h-0 flex-1">
            <Sidebar open={sidebarOpen} onClose={() => setSidebarOpen(false)} />
            {/* Backdrop for the mobile drawer - tap to dismiss. Sits below the sidebar
                (z-40) and above the content, only on phones. */}
            {sidebarOpen && (
              <button
                aria-label="Close sidebar"
                className="fixed inset-x-0 bottom-0 top-[3.25rem] z-30 bg-black/50 md:hidden"
                onClick={() => setSidebarOpen(false)}
              />
            )}
            <EntityView />
          </div>
        </div>
        <DialogHost />
        <UpdateBanner />
        <Toaster richColors position="bottom-right" closeButton />
      </TooltipProvider>
    </StoreProvider>
  );
}
