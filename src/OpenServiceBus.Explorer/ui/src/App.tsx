import { useState } from "react";
import { Toaster } from "sonner";
import { CannedMessagesPage } from "@/components/CannedMessagesPage";
import { EnvironmentsPage } from "@/components/EnvironmentsPage";
import { DialogHost } from "@/components/DialogHost";
import { EntityView } from "@/components/EntityView";
import { Sidebar } from "@/components/Sidebar";
import { Topbar } from "@/components/Topbar";
import { TooltipProvider } from "@/components/ui/tooltip";
import { UpdateBanner } from "@/components/UpdateBanner";
import { useMediaQuery } from "@/lib/useMediaQuery";
import { StoreProvider, useStore } from "@/store";

export default function App() {
  // On phones the sidebar becomes an off-canvas drawer; on md+ it's a static column
  // and this flag is irrelevant (the sidebar is always shown there).
  const [sidebarOpen, setSidebarOpen] = useState(false);
  // Toasts anchor bottom-right on desktop, but on phones that corner sits under the
  // thumb and the browser chrome - pop them from the top there instead.
  const desktopToasts = useMediaQuery("(min-width: 640px)");

  return (
    <StoreProvider>
      <TooltipProvider delayDuration={200}>
        <div className="flex h-full flex-col pb-[max(0.5rem,env(safe-area-inset-bottom))] md:pb-0">
          <Topbar onMenuClick={() => setSidebarOpen((v) => !v)} />
          <div className="relative flex min-h-0 flex-1">
            <Sidebar open={sidebarOpen} onClose={() => setSidebarOpen(false)} />
            {/* Backdrop for the mobile drawer - tap to dismiss. Sits below the sidebar
                (z-40) and above the content, only on phones. Absolute within the content
                container, never viewport-fixed - see the note on the Sidebar drawer. */}
            {sidebarOpen && (
              <button
                aria-label="Close sidebar"
                className="absolute inset-0 z-30 bg-black/50 md:hidden"
                onClick={() => setSidebarOpen(false)}
              />
            )}
            <MainView />
          </div>
        </div>
        <DialogHost />
        <UpdateBanner />
        <Toaster richColors position={desktopToasts ? "bottom-right" : "top-center"} closeButton />
      </TooltipProvider>
    </StoreProvider>
  );
}

function MainView() {
  const store = useStore();
  if (store.view === "canned") return <CannedMessagesPage />;
  if (store.view === "environments") return <EnvironmentsPage />;
  return <EntityView />;
}
