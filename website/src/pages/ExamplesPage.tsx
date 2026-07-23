import { useEffect } from "react";
import { Link } from "react-router-dom";
import Nav from "../components/Nav";
import Examples from "../components/Examples";
import Footer from "../components/Footer";

export default function ExamplesPage() {
  useEffect(() => {
    document.title = "Examples - OpenServiceBus";
  }, []);

  return (
    <>
      <Nav />
      <header className="border-b border-neutral-900 bg-grid min-w-0">
        <div className="mx-auto max-w-5xl px-4 sm:px-6 pt-24 pb-14 sm:pt-32 sm:pb-20">
          <div className="text-xs font-medium text-neutral-500 mb-3">
            <Link to="/" className="hover:text-neutral-300 transition">Home</Link>
            <span className="px-1.5 text-neutral-700">/</span>
            <span className="text-neutral-300">Examples</span>
          </div>
          <h1 className="text-3xl sm:text-5xl font-extrabold tracking-tight">
            <span className="text-white">Code </span>
            <span className="bg-gradient-to-r from-violet-400 to-fuchsia-400 bg-clip-text text-transparent">examples</span>
          </h1>
          <p className="mt-4 max-w-2xl text-neutral-300">
            The same Azure SDK code talks to OpenServiceBus and the real Azure Service Bus
            without modification. Only the connection string changes. Click through to see
            each integration pattern.
          </p>
        </div>
      </header>

      <main className="mx-auto max-w-5xl w-full min-w-0 px-4 sm:px-6">
        <Examples />

        <div className="mb-20 mt-6 rounded-xl border border-neutral-800 bg-neutral-900/40 p-5 sm:p-6">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div>
              <h3 className="text-base font-semibold text-neutral-100">Need more?</h3>
              <p className="text-sm text-neutral-400 mt-1">
                Six runnable sample projects live under <span className="font-mono text-neutral-300">samples/</span> -
                including <span className="font-mono text-neutral-300">samples/NovaBank</span>, a complete
                event-driven banking API with its own test suite, made entirely as an example.
              </p>
            </div>
            <a
              href="https://github.com/mauritsarissen/OpenServiceBus"
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-2 rounded-md border border-neutral-800 bg-neutral-900 px-4 py-2 text-sm font-semibold text-neutral-100 hover:bg-neutral-800 transition w-fit"
            >
              <svg viewBox="0 0 24 24" className="h-4 w-4" fill="currentColor">
                <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.58.1.79-.25.79-.56v-2c-3.2.7-3.87-1.37-3.87-1.37-.52-1.33-1.27-1.69-1.27-1.69-1.04-.71.08-.69.08-.69 1.15.08 1.76 1.18 1.76 1.18 1.03 1.76 2.69 1.25 3.35.96.1-.75.4-1.25.73-1.54-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.29 1.18-3.1-.12-.29-.51-1.46.11-3.05 0 0 .96-.31 3.15 1.18a10.94 10.94 0 0 1 5.74 0c2.19-1.49 3.15-1.18 3.15-1.18.62 1.59.23 2.76.11 3.05.74.81 1.18 1.84 1.18 3.1 0 4.42-2.7 5.4-5.26 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56C20.21 21.39 23.5 17.08 23.5 12 23.5 5.65 18.35.5 12 .5z" />
              </svg>
              View the repo
            </a>
          </div>
        </div>
      </main>

      <Footer />
    </>
  );
}
