import { lazy, Suspense } from "react";
import { BrowserRouter, Routes, Route } from "react-router-dom";
import HomePage from "./pages/HomePage";
import NotFoundPage from "./pages/NotFoundPage";

// The Examples page carries every code sample in four languages - by far the largest
// page-specific payload. It is lazy on the client so the homepage bundle stays lean;
// the server entry imports it statically for prerendering (entry-server.tsx).
const ExamplesPage = lazy(() => import("./pages/ExamplesPage"));

export function PageShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-neutral-950 text-neutral-100 flex flex-col">
      <Suspense fallback={null}>{children}</Suspense>
    </div>
  );
}

export function AppRoutes() {
  return (
    <PageShell>
      <Routes>
        <Route path="/" element={<HomePage />} />
        <Route path="/examples" element={<ExamplesPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Routes>
    </PageShell>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <AppRoutes />
    </BrowserRouter>
  );
}
