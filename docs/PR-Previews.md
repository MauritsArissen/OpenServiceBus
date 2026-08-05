# PR Preview Environments

Every open pull request into `main` gets its own live environment at
`https://pr-<number>.openservicebus.net` - for example PR #43 runs at
`pr-43.openservicebus.net`. Use it to try a change on real devices (phone, tablet,
another browser) without checking anything out locally.

## What a preview contains

The same demo-style setup as [demo.openservicebus.net](https://demo.openservicebus.net):
the broker and the Explorer UI built from the PR's code, plus the demo seeder generating
live topology and traffic, wiping and reseeding on the usual reset boundaries.

- The Explorer runs in demo mode: the connection is pinned to the co-located broker and
  the connection inputs are locked, so a public preview can never be pointed at another
  host.
- Storage is in-memory - state is throwaway and resets on every redeploy.
- Only the Explorer's HTTP surface is public (behind Cloudflare). The AMQP port is not
  reachable from outside; the Explorer backend talks to the broker inside the container.

## Lifecycle

| PR event | Effect |
| --- | --- |
| Opened / reopened | Image built, pushed to a private GHCR package, deployed |
| New push to the branch | Rebuilt and redeployed in place (superseded builds are cancelled) |
| Closed or merged | Containers, nginx config, and image tags removed |

The `PR preview` workflow (`.github/workflows/pr-preview.yml`) posts and maintains a
comment on the PR with the preview URL and the deployed commit. It is fully separate
from the CI workflow - a red build does not block the preview and vice versa.

Previews only run for branches in this repository. PRs from forks are skipped: the
workflow needs deploy credentials that fork-triggered runs never receive.

## Images

Preview images are pushed to `ghcr.io/mauritsarissen/openservicebus-preview` (and
`...-preview-seeder`), tagged `pr-<number>`. These packages are private and separate
from the public `openservicebus` release image; tags are deleted when the PR closes.
