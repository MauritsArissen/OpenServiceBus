import CopyButton from "./CopyButton";

const DOCKER_CMD =
  "docker run --rm -p 5672:5672 -p 5300:5300 -p 5400:5400 mauritsarissen/openservicebus:latest";

const CONN_STRING =
  "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=anykey;UseDevelopmentEmulator=true";

export default function Quickstart() {
  return (
    <section id="quickstart" className="py-16 sm:py-24">
      <div className="mb-8">
        <h2 className="text-3xl sm:text-4xl font-bold tracking-tight">Up and running in seconds</h2>
        <p className="mt-3 text-neutral-400">
          One Docker command. The broker, management REST API, and Explorer UI are bundled -
          leave it running and develop your application against it like it's Azure.
        </p>
      </div>

      <div className="rounded-xl border border-neutral-800 bg-neutral-900/60 p-4 sm:p-5">
        <div className="flex items-center justify-between gap-3 mb-3">
          <div className="flex items-center gap-2 min-w-0">
            <span className="h-2.5 w-2.5 rounded-full bg-red-500/70 shrink-0" />
            <span className="h-2.5 w-2.5 rounded-full bg-amber-500/70 shrink-0" />
            <span className="h-2.5 w-2.5 rounded-full bg-emerald-500/70 shrink-0" />
            <span className="ml-2 text-xs text-neutral-400 font-mono truncate">terminal</span>
          </div>
          <CopyButton text={DOCKER_CMD} />
        </div>
        <pre className="overflow-x-auto font-mono text-[12.5px] sm:text-sm leading-relaxed text-neutral-100">
          <span className="text-violet-400 select-none">$ </span>
          <span>{DOCKER_CMD}</span>
        </pre>
      </div>

      <div className="mt-6 grid gap-3 sm:grid-cols-3">
        <EndpointCard label="AMQP broker" value="localhost:5672" />
        <EndpointCard label="Management REST" value="http://localhost:5300" />
        <EndpointCard label="Explorer UI" value="http://localhost:5400" />
      </div>

      <div className="mt-8">
        <div className="mb-2 flex items-center justify-between gap-3">
          <p className="text-sm text-neutral-400">Connection string for the Azure SDK</p>
          <CopyButton text={CONN_STRING} />
        </div>
        <div className="rounded-lg border border-neutral-800 bg-neutral-900/40 p-3">
          <code className="block font-mono text-xs sm:text-sm text-neutral-300 break-all">{CONN_STRING}</code>
        </div>
      </div>
    </section>
  );
}

function EndpointCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-neutral-800 bg-neutral-900/40 p-3 min-w-0">
      <div className="text-[11px] uppercase tracking-wide text-neutral-400 font-semibold">{label}</div>
      <div className="mt-1 font-mono text-sm text-neutral-200 break-all">{value}</div>
    </div>
  );
}
