export default function Hero() {
  return (
    <header className="bg-grid border-b border-neutral-900 min-w-0">
      <div className="mx-auto max-w-5xl px-4 sm:px-6 pt-20 pb-16 sm:pt-24 sm:pb-28">
        <div className="flex flex-wrap items-center gap-2 text-xs font-medium text-neutral-400 mb-6">
          <span className="rounded-full border border-neutral-800 bg-neutral-900/60 px-2.5 py-1">MIT licensed</span>
          <span className="rounded-full border border-neutral-800 bg-neutral-900/60 px-2.5 py-1">.NET 8 &amp; .NET 10</span>
          <span className="rounded-full border border-neutral-800 bg-neutral-900/60 px-2.5 py-1">AMQP 1.0</span>
        </div>
        <h1 className="text-4xl sm:text-6xl font-extrabold tracking-tight break-words">
          <span className="text-white">Open</span>
          <span className="bg-gradient-to-r from-violet-400 to-fuchsia-400 bg-clip-text text-transparent">ServiceBus</span>
        </h1>
        <p className="mt-5 max-w-2xl text-lg sm:text-xl text-neutral-300 leading-relaxed">
          A lightweight, open-source Azure Service Bus emulator for your tests and your local dev loop.
          Drop it into unit tests as a NuGet package, or spin it up with a single Docker command and run
          your application against a local service bus - Explorer UI included. The Azure SDK talks to it
          unchanged.
        </p>
        <div className="mt-8 flex flex-wrap gap-3">
          <a
            href="#quickstart"
            className="inline-flex items-center gap-2 rounded-md bg-violet-600 px-5 py-2.5 text-sm font-semibold text-white shadow-sm transition hover:bg-violet-500"
          >
            Get started
            <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2.5">
              <path d="M5 12h14M13 5l7 7-7 7" />
            </svg>
          </a>
          <a
            href="https://github.com/mauritsarissen/OpenServiceBus"
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-2 rounded-md border border-neutral-800 bg-neutral-900/60 px-5 py-2.5 text-sm font-semibold text-neutral-100 transition hover:bg-neutral-800"
          >
            <svg viewBox="0 0 24 24" className="h-4 w-4" fill="currentColor">
              <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.58.1.79-.25.79-.56v-2c-3.2.7-3.87-1.37-3.87-1.37-.52-1.33-1.27-1.69-1.27-1.69-1.04-.71.08-.69.08-.69 1.15.08 1.76 1.18 1.76 1.18 1.03 1.76 2.69 1.25 3.35.96.1-.75.4-1.25.73-1.54-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.29 1.18-3.1-.12-.29-.51-1.46.11-3.05 0 0 .96-.31 3.15 1.18a10.94 10.94 0 0 1 5.74 0c2.19-1.49 3.15-1.18 3.15-1.18.62 1.59.23 2.76.11 3.05.74.81 1.18 1.84 1.18 3.1 0 4.42-2.7 5.4-5.26 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56C20.21 21.39 23.5 17.08 23.5 12 23.5 5.65 18.35.5 12 .5z"/>
            </svg>
            View on GitHub
          </a>
        </div>
      </div>
    </header>
  );
}
