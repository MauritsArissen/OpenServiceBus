import { useEffect } from "react";
import { Link } from "react-router-dom";
import Nav from "../components/Nav";
import Footer from "../components/Footer";

export default function NotFoundPage() {
  useEffect(() => {
    document.title = "404 - OpenServiceBus";
    // The path doesn't exist on disk, so search engines shouldn't index whatever
    // URL was typed. The SPA serves with HTTP 200 (Nginx falls back to index.html),
    // so this meta tag is the best signal we can give.
    const meta = document.createElement("meta");
    meta.name = "robots";
    meta.content = "noindex,nofollow";
    document.head.appendChild(meta);
    return () => {
      meta.remove();
      document.title = "OpenServiceBus - Azure Service Bus emulator";
    };
  }, []);

  return (
    <>
      <Nav />
      <main className="flex-1 w-full min-w-0 bg-grid border-b border-neutral-900">
        <div className="mx-auto max-w-3xl px-4 sm:px-6 py-20 sm:py-32 text-center">
          <div className="text-7xl sm:text-8xl font-extrabold bg-gradient-to-r from-violet-400 to-fuchsia-400 bg-clip-text text-transparent">
            404
          </div>
          <h1 className="mt-6 text-2xl sm:text-3xl font-bold text-white">
            Page not found
          </h1>
          <p className="mt-3 text-neutral-400 max-w-md mx-auto">
            The page you're looking for doesn't exist on{" "}
            <code className="font-mono text-neutral-300">
              openservicebus.net
            </code>
            .
          </p>
          <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
            <Link
              to="/"
              className="inline-flex items-center gap-2 rounded-md bg-violet-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-violet-500 transition"
            >
              <svg
                viewBox="0 0 24 24"
                className="h-4 w-4"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.5"
              >
                <path d="M19 12H5M12 19l-7-7 7-7" />
              </svg>
              Back to home
            </Link>
            <Link
              to="/examples"
              className="inline-flex items-center gap-2 rounded-md border border-neutral-800 bg-neutral-900/60 px-5 py-2.5 text-sm font-semibold text-neutral-100 hover:bg-neutral-800 transition"
            >
              View examples
            </Link>
          </div>
        </div>
      </main>
      <Footer />
    </>
  );
}
