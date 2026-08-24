import { renderToString } from "react-dom/server";
import { StaticRouter } from "react-router";
import { Routes, Route } from "react-router-dom";
import { PageShell } from "./App";
import HomePage from "./pages/HomePage";
import ExamplesPage from "./pages/ExamplesPage";
import NotFoundPage from "./pages/NotFoundPage";

export function render(url: string): string {
  return renderToString(
    <StaticRouter location={url}>
      <PageShell>
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/examples" element={<ExamplesPage />} />
          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </PageShell>
    </StaticRouter>,
  );
}
