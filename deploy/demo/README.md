# Live demo - demo.openservicebus.net

A hosted, always-on Explorer wired to a self-resetting OpenServiceBus instance. The
connection is locked, and the environment wipes + reseeds every 30 minutes.

## What runs

`docker-compose.yml` brings up two containers:

- **openservicebus** - the published `mauritsarissen/openservicebus:latest` image (broker +
  Explorer UI). Runs in **demo mode** (`OSB_EXPLORER_DEMO=true`): the Explorer's connection
  inputs are grayed out ("Not changeable in the live demo") and a reset countdown shows in
  the top bar. Only the Explorer port (5400) is exposed, on loopback, for nginx.
- **seeder** - `mauritsarissen/openservicebus-demo-seeder:latest`. Creates the demo topology
  (2 queues + 1 topic with 3 subscriptions, two SQL-filtered), drives fluctuating load that
  never exceeds ~450 active messages (sending, completing, dead-lettering, DLQ cleanup,
  deferring, abandoning), and wipes + recreates everything on each 30-minute boundary.

The reset cadence (`*_RESET_INTERVAL_SECONDS`) is shared by both - they align to wall-clock
boundaries (:00 / :30 UTC), so the countdown and the actual reset agree with no coordination.

## Deploy

This is the **final job of `release.yml`** (`deploy-demo`), so it runs only after an actual
version/tag release - never on an ordinary push to `main`. Once the broker image is
published, it builds/pushes the seeder image, rsyncs this folder to
`/opt/openservicebus-demo/` on the VPS, and runs `docker compose pull && up -d`, picking up
the just-published `:latest`. To redeploy the demo between releases, re-run the latest
**Release** workflow run from the Actions tab.

## One-time VPS setup (manual)

The VPS already has everything installed (Docker + compose plugin, nginx, and certbot with
its auto-renew timer active - the same stack that serves openservicebus.net). Only these
one-time steps remain; after them everything stays current automatically.

1. **DNS** (Cloudflare): a **proxied** (orange-cloud) record `demo.openservicebus.net` → the
   VPS origin IP. Keeping it proxied is what hides the origin IP - public DNS returns
   Cloudflare's addresses, never the server's. Set the zone's SSL/TLS mode to
   **Full (strict)** so Cloudflare talks to the origin over the Let's Encrypt cert below.
2. **First deploy**: cut a release (so `openservicebus:latest` includes demo mode). The
   `deploy-demo` job at the end of `release.yml` rsyncs this folder to
   `/opt/openservicebus-demo/` and runs `docker compose up -d`. The containers are now live
   on `127.0.0.1:5400`.
3. **Reverse proxy + TLS** ([`nginx-demo.conf`](nginx-demo.conf)) on the VPS:
   ```bash
   sudo cp /opt/openservicebus-demo/nginx-demo.conf /etc/nginx/sites-available/demo.openservicebus.net
   sudo ln -s /etc/nginx/sites-available/demo.openservicebus.net /etc/nginx/sites-enabled/
   sudo nginx -t && sudo systemctl reload nginx
   sudo certbot --nginx -d demo.openservicebus.net   # adds the 443 block; auto-renews after
   ```
   **Cloudflare + certbot note:** the HTTP-01 challenge must reach the origin. If certbot
   fails while the record is proxied, temporarily set it to **DNS only** (grey cloud) in
   Cloudflare, run certbot, then switch it back to **Proxied** (orange). certbot's systemd
   timer (already active) renews the cert from then on.

After this, nothing else is manual: every release redeploys the containers (the `deploy-demo`
job in `release.yml`), certbot renews TLS, and the nginx vhost + DNS are permanent.

Required repo secrets (already present for the website/release workflows): `DOCKERHUB_USERNAME`,
`DOCKERHUB_TOKEN`, `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY`.

## Run it locally

```bash
docker compose -f deploy/demo/docker-compose.yml up -d   # from the repo root
# Explorer: http://localhost:5400  (in demo mode)
```
