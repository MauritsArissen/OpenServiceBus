type Row = {
  feature: string;
  osb: string | boolean | "partial";
  ms: string | boolean | "partial";
  note?: string;
};

const ROWS: Row[] = [
  { feature: "License", osb: "MIT", ms: "EULA-restricted" },
  {
    feature: "Dependencies",
    osb: "Single binary / image",
    ms: "Docker + SQL Edge",
  },
  { feature: "Image size", osb: "~300 MB Alpine", ms: "~1 GB+" },
  { feature: "Cold-start time", osb: "< 1 second", ms: "30-60 seconds" },
  { feature: "Embeddable in tests (NuGet)", osb: true, ms: false },
  { feature: "Bundled Explorer UI", osb: true, ms: false },
  {
    feature: "Persistence options",
    osb: "In-memory or SQLite",
    ms: "SQL Edge (required)",
  },
  { feature: "Queues", osb: true, ms: true },
  { feature: "Topics & Subscriptions", osb: true, ms: true },
  { feature: "SQL filters", osb: "partial", ms: true },
  { feature: "Sessions", osb: true, ms: true },
  { feature: "Duplicate detection", osb: true, ms: true },
  { feature: "Auto-forwarding", osb: true, ms: true },
  { feature: "Transactions", osb: "partial", ms: true },
  { feature: "SAS signature verification", osb: "partial", ms: true },
  { feature: "AMQP-over-WebSockets", osb: true, ms: true },
  { feature: "OpenTelemetry tracing & metrics", osb: true, ms: false },
];

export default function Comparison() {
  return (
    <section className="pb-24 sm:pb-32">
      <div className="mb-10">
        <h2 className="text-3xl sm:text-4xl font-bold tracking-tight">
          Why OpenServiceBus?
        </h2>
        <p className="mt-3 text-neutral-400 max-w-2xl">
          Microsoft ships an official Azure Service Bus emulator - but it
          requires Docker, Microsoft SQL Edge, and a per-use EULA. That's
          overkill if you just want to spin up a broker in a unit-test fixture.
          OpenServiceBus is a single Alpine binary you can embed as a NuGet
          package or run via one Docker command. MIT-licensed. Talks real AMQP
          1.0 so the Azure SDK works unchanged.
        </p>
      </div>

      {/* Mobile (< sm): a stacked-card layout. Each feature is its own card with the two
          values side-by-side underneath the label. Avoids the horizontal scroll trap a
          three-column table forces onto a phone-sized viewport. */}
      <div className="space-y-2 sm:hidden">
        {ROWS.map((row) => (
          <div
            key={row.feature}
            className="rounded-lg border border-neutral-800 bg-neutral-900/40 p-3"
          >
            <div className="text-sm font-medium text-neutral-100 mb-2 break-words">
              {row.feature}
            </div>
            <div className="grid grid-cols-2 gap-3 text-xs">
              <div>
                <div className="text-[10px] uppercase tracking-wider text-violet-300 mb-1 font-semibold">
                  OpenServiceBus
                </div>
                <Cell value={row.osb} />
              </div>
              <div>
                <div className="text-[10px] uppercase tracking-wider text-neutral-400 mb-1 font-semibold">
                  MS Emulator
                </div>
                <Cell value={row.ms} />
              </div>
            </div>
          </div>
        ))}
      </div>

      {/* Tablet+ (sm:): traditional three-column table. */}
      <div className="hidden sm:block overflow-hidden rounded-xl border border-neutral-800">
        <table className="w-full text-sm border-collapse">
          <thead className="bg-neutral-900/80 text-left">
            <tr className="border-b border-neutral-800">
              <th className="px-4 py-3 font-semibold text-neutral-300">Feature</th>
              <th className="px-4 py-3 font-semibold">
                <span className="text-violet-300">OpenServiceBus</span>
              </th>
              <th className="px-4 py-3 font-semibold text-neutral-400">
                Microsoft Emulator
              </th>
            </tr>
          </thead>
          <tbody>
            {ROWS.map((row, i) => (
              <tr
                key={row.feature}
                className={`border-b border-neutral-900 last:border-0 ${i % 2 === 1 ? "bg-neutral-950" : "bg-neutral-900/30"}`}
              >
                <td className="px-4 py-3 text-neutral-300">{row.feature}</td>
                <td className="px-4 py-3">
                  <Cell value={row.osb} />
                </td>
                <td className="px-4 py-3">
                  <Cell value={row.ms} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <p className="mt-4 text-xs text-neutral-400">
        Comparison reflects feature coverage as of the latest release. "Partial"
        means the feature is wired but lags the official emulator in a
        documented way - see the project README for specifics.
      </p>
    </section>
  );
}

function Cell({ value }: { value: string | boolean | "partial" }) {
  if (value === true) {
    return (
      <span className="inline-flex items-center gap-1.5 text-emerald-400">
        <svg
          viewBox="0 0 24 24"
          className="h-4 w-4"
          fill="none"
          stroke="currentColor"
          strokeWidth="3"
        >
          <path d="M20 6 9 17l-5-5" />
        </svg>
        Yes
      </span>
    );
  }
  if (value === false) {
    return (
      <span className="inline-flex items-center gap-1.5 text-neutral-400">
        <svg
          viewBox="0 0 24 24"
          className="h-4 w-4"
          fill="none"
          stroke="currentColor"
          strokeWidth="2.5"
        >
          <path d="M18 6 6 18M6 6l12 12" />
        </svg>
        No
      </span>
    );
  }
  if (value === "partial") {
    return (
      <span className="inline-flex items-center gap-1.5 text-amber-400">
        <svg
          viewBox="0 0 24 24"
          className="h-4 w-4"
          fill="none"
          stroke="currentColor"
          strokeWidth="2.5"
        >
          <path d="M12 6v6M12 17h.01" />
          <circle cx="12" cy="12" r="10" />
        </svg>
        Partial
      </span>
    );
  }
  return <span className="text-neutral-200">{value}</span>;
}
