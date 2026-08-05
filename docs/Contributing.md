# Contributing

Contributions of any size welcome - bug fixes, doc improvements, new milestones from the
[Roadmap](ROADMAP), or whatever Service Bus surface you find missing.

## Repo layout

```
src/
  OpenServiceBus.Core/               Domain types + abstractions. ZERO NuGet deps.
  OpenServiceBus.InMemoryStorage/    Default in-memory store + registries + router + tx manager
  OpenServiceBus.SqliteStorage/      Persistent IMessageStore (SQLite)
  OpenServiceBus.Amqp/               AMQP 1.0 listener + protocol mapping
  OpenServiceBus.Management/         REST management API
  OpenServiceBus.Host/               Standalone executable (Kestrel + everything wired)
  OpenServiceBus.Testing/            Embeddable broker for test fixtures
  OpenServiceBus.Explorer/           Browser-based UI (ASP.NET Core + static HTML)

tests/
  OpenServiceBus.Core.Tests/                       Unit tests for filters, config loader, etc.
  OpenServiceBus.InMemoryStorage.Tests/            In-memory store mechanics
  OpenServiceBus.SqliteStorage.Tests/              SQLite parity + SDK round-trip
  OpenServiceBus.Amqp.Tests/                       Per-handler unit tests
  OpenServiceBus.Amqp.WireTests/                   AMQPNetLite client vs our broker - wire-level
  OpenServiceBus.IntegrationTests/                 Azure SDK end-to-end (the headline suite)
  OpenServiceBus.AzureFunctions.IntegrationTests/  Real Functions worker with ServiceBusTrigger
  OpenServiceBus.Testing.Tests/                    Tests for the embeddable host itself

samples/
  OpenServiceBus.Samples.QuickStart/             Console send/receive
  OpenServiceBus.Samples.TopicsAndFilters/       Pub-sub with SQL + correlation filters
  OpenServiceBus.Samples.Sessions/               Session-locked workers
  OpenServiceBus.Samples.WorkerService/          Background-worker pattern via Microsoft.Extensions.Hosting
  OpenServiceBus.Samples.Functions/              Minimal Azure Functions ServiceBusTrigger (integration test target)
  OpenServiceBus.Samples.FunctionsTriggerDemo/   Interactive multi-trigger Functions app
  config.sample.json

docs/                Wiki source - synced to GitHub Wiki via the wiki-sync workflow
.github/workflows/   ci.yml + release.yml + wiki.yml
Dockerfile
docker-compose.yml
```

## Build + test

```bash
dotnet restore
dotnet build --configuration Release
dotnet test  --configuration Release --no-build
```

Multi-targets:

- Libraries: `net8.0` + `net10.0`
- Executable host + Explorer: `net10.0`

Full regression takes ~30s on a modern laptop. The Azure Functions test needs
`func` (Azure Functions Core Tools v4) and the .NET 8 runtime on PATH; it skips when
either is missing.

## Conventions

- **Treat warnings as errors** (set in `Directory.Build.props`). Zero `#pragma warning disable` outside of generated code.
- **Nullable enabled** everywhere.
- **xUnit + Shouldly** for tests. Three-section format: `// Arrange`, `// Act`, `// Assert`.
- **No `Task.Delay` in tests.** Time-sensitive tests use `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`.
- **Add a test for every milestone gate.** SDK-level integration tests over wire/unit tests when possible - they protect against protocol drift.
- **Document non-obvious comments.** WHY a line exists, not what it does. Reference the SDK source line if you decompiled to figure something out.
- **Single-responsibility assemblies.** Adding a new dependency on a heavyweight package (e.g. EF Core, gRPC) should go in a new optional assembly so `OpenServiceBus.Core` and `OpenServiceBus.Testing` stay light.

## PR previews

Every open PR into `main` gets a live demo environment at
`https://pr-<number>.openservicebus.net`, redeployed on every push and removed on
close - see [PR Previews](PR-Previews).

## Adding a milestone

The roadmap is in [`docs/ROADMAP.md`](ROADMAP). When picking up a new milestone:

1. Open a draft PR early. Reference the milestone (M-number) in the title.
2. Add an SDK-level integration test that proves the feature works against the real
   `Azure.Messaging.ServiceBus` client.
3. Add a documentation page in `docs/` (hyphen-cased filename for wiki compatibility).
4. Update the README's feature matrix.
5. If the feature touches the storage layer (`IMessageStore`), also add SQLite parity
   tests.

## Releasing

Releases are tag-driven. Push a `v1.2.3` tag (or run the `release.yml` workflow manually
with a version input) and the pipeline:

1. Runs `dotnet test` (must be green).
2. Packs the six library NuGets with the tag's version.
3. Pushes them to **[nuget.org](https://www.nuget.org)** - the public, primary registry.
4. Mirrors the same NuGets to **GitHub Packages** (`https://nuget.pkg.github.com/mauritsarissen`)
   as a secondary so private/preview installs work without a nuget.org account.
5. Builds the **multi-arch Docker image** (linux/amd64 + linux/arm64).
6. Pushes the image to **GHCR** and **Docker Hub** tagged `:1.2.3` + `:latest`.
7. Publishes a GitHub Release with auto-generated release notes.

Required secrets (set in repo settings → Secrets and variables → Actions):

| Secret               | Where to get it                                                                                                   |
| -------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `NUGET_API_KEY`      | <https://www.nuget.org/account/apikeys> - scope `Push new packages and package versions`, glob `OpenServiceBus.*` |
| `DOCKERHUB_USERNAME` | Your Docker Hub login                                                                                             |
| `DOCKERHUB_TOKEN`    | <https://hub.docker.com/settings/security> - PAT with `Read & Write`, **not** your password                       |

GHCR uses the built-in `GITHUB_TOKEN` - no setup needed. If `NUGET_API_KEY` isn't set the
nuget.org push step skips itself gracefully; everything else still runs.

## Wiki sync

The [`wiki.yml`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/.github/workflows/wiki.yml)
workflow mirrors `docs/*.md` to the repository's wiki on every push to `main` that
touches the docs folder. Edit pages in `docs/` (not in the wiki directly) - the wiki is a
build artifact.

## Code of conduct

Be excellent to each other.

## License

Contributions are accepted under the same [MIT license](https://github.com/mauritsarissen/OpenServiceBus/blob/main/LICENSE)
as the rest of the project.
