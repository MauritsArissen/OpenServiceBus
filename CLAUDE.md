# OpenServiceBus

MIT-licensed, embeddable Azure Service Bus emulator for .NET. Speaks real AMQP 1.0 (TCP +
WebSocket) and the ATOM management API on one port, so `Azure.Messaging.ServiceBus` -
including `ServiceBusAdministrationClient` - and the other official SDKs connect unmodified.
Ships as the `OpenServiceBus.Testing` NuGet (in-process broker for tests), a Docker image,
and a standalone host with a browser Explorer UI. Feature surface: queues, topics +
subscriptions (SQL/correlation filters), sessions, duplicate detection, auto-forwarding,
transactions, scheduling, TTL, peek-lock, dead-lettering, OpenTelemetry.

Layout: broker code in `src/` (Core = protocol-agnostic model, Amqp = wire, \*Storage =
stores, Management.Atom = admin API, Testing = embeddable host), tests in `tests/`
(including cross-SDK smokes in `tests/sdk-smoke/`), docs in `docs/`.

## Conventions for Claude

- **No comments**: By default do not put comments inside of the code base, rather if its
  an functional change it must be documented in the docs, rather than the code.
- **PR titles** must follow the conventional style checked by the `PR title` workflow:
  `type: summary` with type one of `feat|fix|chore|docs|test|refactor|perf|ci|build`
  (optional `(scope)`). The repo squash-merges, so the PR title becomes the commit title
  on `main` - write it like the existing history.
- **PR descriptions**: always write a proper description - what changed, why, and how it
  was tested. Never mention Claude, AI, or "generated with" anything in PR descriptions
  or commit messages.
- **No AI co-author**: never add a `Co-Authored-By: Claude ...` (or any AI) trailer to
  commits.
- **Dashes**: never use em dashes or en dashes anywhere (code, comments, docs, PRs).
  Only the plain hyphen-minus character.
- **Unit tests for every new feature**: each new feature lands with unit tests for its
  core logic (parser, routing, storage, codec, ...), in the test project matching the
  layer it lives in - not only SDK-level integration tests.
- **Every feature/fix reaches the cross-SDK smokes**: when a change affects what the
  broker speaks on the wire or through the management surface, extend all four
  `tests/sdk-smoke/` scripts (.NET, Node.js, Java, Python) with a step exercising it,
  so it is verified against the four main languages and SDKs on every PR. Keep the
  canonical step sequence identical across the four scripts.
- Run `dotnet test` on the full solution before opening a PR; `dotnet test
  samples/dotnet/NovaBank/NovaBank.slnx` is the realistic end-to-end check when broker
  delivery/session/peek behaviour changes.
