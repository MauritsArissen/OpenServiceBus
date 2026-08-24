type QA = { q: string; a: React.ReactNode };

const FAQS: QA[] = [
  {
    q: "Is OpenServiceBus production-ready?",
    a: (
      <>
        No - it's built for the inner loop: unit tests, CI, and running your
        application locally against a faithful broker while you develop. Use the
        real Azure Service Bus in production. OpenServiceBus's positioning is
        "real AMQP 1.0 behavior without the Docker + SQL Server + EULA overhead."
      </>
    ),
  },
  {
    q: "Can I run my application against it during local development?",
    a: (
      <>
        Yes - that's half the point. Start the container next to your app (a{" "}
        <code>docker compose</code> service works great), point your connection
        string at <code>localhost</code>, and develop against a broker that
        behaves like Azure: queues, topics with SQL filters, sessions,
        scheduling, dead-lettering. The Docker image persists to SQLite by
        default so messages survive restarts, <code>config.json</code> declares
        your entities so every developer boots the same topology, and the
        Explorer on port <code>5400</code> lets you watch and manipulate
        everything your app is doing while you debug.
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
        All four official Azure Service Bus SDKs: .NET{" "}
        (<code>Azure.Messaging.ServiceBus</code>), Node.js{" "}
        (<code>@azure/service-bus</code>), Java{" "}
        (<code>azure-messaging-servicebus</code>) and Python{" "}
        (<code>azure-servicebus</code>). Every pull request runs an identical
        smoke sequence against the broker in all four stacks - send, peek,
        settle, sessions, scheduling, entity CRUD, dead-lettering, SQL filters
        and more - so cross-SDK behavior is verified continuously, not assumed.
      </>
    ),
  },
  {
    q: "Does ServiceBusAdministrationClient work?",
    a: (
      <>
        Yes - OpenServiceBus serves the real ATOM management API on the AMQP
        port, so the .NET admin client creates, inspects, updates and deletes
        queues, topics, subscriptions and rules at runtime, exactly like
        against Azure. The Java / JS / Python admin clients currently hardcode
        https and cannot target a plaintext local endpoint (an SDK limitation,
        not a broker one) - from those stacks use plain HTTP against the same
        ATOM API, <code>config.json</code>, or the Explorer.
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
    a: (
      <>
        All four are supported, same APIs you'd use against real Service Bus -
        plus duplicate detection, auto-forwarding, transfer dead-letter queues,
        transactions, and full SQL filter grammar (arithmetic, LIKE...ESCAPE,
        parameters, built-in functions).
      </>
    ),
  },
  {
    q: "Is the Explorer UI included?",
    a: (
      <>
        Yes. The Docker image bundles a web Explorer on port <code>5400</code> -
        browse queues and topics, send messages, receive with real peek-locks,
        multi-select for bulk complete / abandon / defer / dead-letter, resend
        or requeue dead-lettered messages, purge entities, export messages as
        JSON, edit subscription rules with the full SQL syntax reference, and
        watch live throughput metrics. It's a real Azure SDK client under the
        hood, so every action you take in the UI exercises the same code path
        your application would. Try it at{" "}
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
    q: "Can I save reusable test messages and share them with my team?",
    a: (
      <>
        Yes - the Explorer has <strong>canned messages</strong>: save a fully
        configured message (body, system properties, application properties,
        copies, strategy) under a name and replay it with one click. Payloads
        can use <strong>dynamic variables</strong> like <code>{"{{$guid}}"}</code>,{" "}
        <code>{"{{$datetime iso8601 -5d}}"}</code>, <code>{"{{$sequence}}"}</code>{" "}
        and <code>{"{{$randomInt 1 100}}"}</code> - resolved fresh for every copy
        of a multi-count send - and <strong>Postman-style environments</strong>{" "}
        (<code>{"{{cardnumber}}"}</code> with a switchable active set; Postman
        environment exports import directly). Point{" "}
        <code>OSB_EXPLORER_CANNED_FILE</code> and{" "}
        <code>OSB_EXPLORER_ENVIRONMENTS_FILE</code> at JSON files mounted in
        docker compose and the whole setup is committed to git: edits in the UI
        write back to the files, and a reload picks up a <code>git pull</code>{" "}
        without restarting. Because values are materialized before the SDK send,
        all of it also works against a real Azure Service Bus namespace.
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
