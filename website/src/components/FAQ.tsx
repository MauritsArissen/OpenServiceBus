type QA = { q: string; a: React.ReactNode };

const FAQS: QA[] = [
  {
    q: "Is OpenServiceBus production-ready?",
    a: (
      <>
        No - it's designed for local development, unit tests, and CI. Use the real
        Azure Service Bus in production. OpenServiceBus's positioning is "real AMQP
        1.0 behavior without the Docker + SQL Server + EULA overhead."
      </>
    ),
  },
  {
    q: "Does it work with Azure Functions?",
    a: (
      <>
        Yes. The repo ships an integration test that boots a real isolated-worker
        Functions app with a <code>ServiceBusTrigger</code> pointed at an
        OpenServiceBus instance, then asserts 100 messages flow trigger → handler
        → complete.
      </>
    ),
  },
  {
    q: "Which SDKs / languages does it support?",
    a: (
      <>
        The .NET <code>Azure.Messaging.ServiceBus</code> SDK is verified end-to-end.
        Because OpenServiceBus speaks real AMQP 1.0, the Java / Python / JS SDKs
        should work too - they're just not part of CI yet. Cross-language coverage
        is on the roadmap.
      </>
    ),
  },
  {
    q: "Where does the data live?",
    a: (
      <>
        The NuGet test fixture and the standalone host default to in-memory -
        gone on restart, perfect for tests. The Docker image defaults to
        SQLite at <code>/data/broker.db</code>, so messages survive container
        recreates when you mount a volume. You can flip either mode with{" "}
        <code>OpenServiceBus:Storage:Mode</code>. Entity settings (lock
        duration, sessions, forwarding) are not stored in SQLite - declare
        them in a <code>config.json</code> so they survive restarts too.
      </>
    ),
  },
  {
    q: "What about sessions, TTL, scheduled messages, and DLQ?",
    a: <>All four are supported. Same APIs you'd use against real Service Bus.</>,
  },
  {
    q: "Is the Explorer UI included?",
    a: (
      <>
        Yes. The Docker image bundles a web Explorer on port <code>5400</code> -
        browse queues and topics, send messages, receive with real peek-locks,
        drain and requeue DLQs, edit subscription rules, and watch live
        throughput metrics. It's a real Azure SDK client under the hood, so
        every action you take in the UI exercises the same code path your
        application would. Try it at{" "}
        <a
          href="https://demo.openservicebus.net"
          target="_blank"
          rel="noreferrer"
          className="text-violet-300 underline underline-offset-2"
        >
          demo.openservicebus.net
        </a>
        .
      </>
    ),
  },
  {
    q: "How does it differ from the Microsoft emulator?",
    a: (
      <>
        MIT-licensed (no EULA), a single ~300&nbsp;MB Alpine image with no SQL
        Edge dependency, embeddable as a NuGet test fixture, sub-second cold
        start, plus extras the official emulator lacks: a bundled Explorer UI,
        AMQP-over-WebSocket, and native OpenTelemetry. See the comparison table
        on the home page for the full breakdown.
      </>
    ),
  },
  {
    q: "Can I use it in CI/CD?",
    a: (
      <>
        Yes - it's a primary use case. Either spin up the Docker image in a
        service container, or use the <code>OpenServiceBus.Testing</code> NuGet
        fixture for an in-process broker your tests can talk to directly.
      </>
    ),
  },
];

export default function FAQ() {
  return (
    <section className="pb-24 sm:pb-32">
      <div className="mb-8">
        <h2 className="text-3xl sm:text-4xl font-bold tracking-tight">Frequently asked</h2>
        <p className="mt-3 text-neutral-400 max-w-2xl">
          The things people usually ask before they pick up a Service Bus emulator.
        </p>
      </div>

      <div className="rounded-xl border border-neutral-800 overflow-hidden divide-y divide-neutral-900">
        {FAQS.map((qa, i) => (
          <details
            key={i}
            className="group bg-neutral-900/40 open:bg-neutral-900/70 transition-colors"
          >
            <summary className="flex items-center justify-between cursor-pointer list-none px-5 py-4 text-neutral-100 font-medium select-none">
              <span>{qa.q}</span>
              <svg
                viewBox="0 0 24 24"
                className="h-4 w-4 text-neutral-500 transition-transform group-open:rotate-180"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
              >
                <path d="m6 9 6 6 6-6" />
              </svg>
            </summary>
            <div className="px-5 pb-4 -mt-1 text-sm text-neutral-300 leading-relaxed [&_code]:font-mono [&_code]:text-[12.5px] [&_code]:bg-neutral-950 [&_code]:px-1.5 [&_code]:py-0.5 [&_code]:rounded [&_code]:text-violet-300">
              {qa.a}
            </div>
          </details>
        ))}
      </div>
    </section>
  );
}
