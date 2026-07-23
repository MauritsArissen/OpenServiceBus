# Live demo — demo.openservicebus.net

A hosted, always-on Explorer wired to a self-resetting OpenServiceBus instance. The
connection is locked, and the environment wipes + reseeds every 30 minutes.

## What runs

`docker-compose.yml` brings up two containers:

- **openservicebus** — the published `mauritsarissen/openservicebus:latest` image (broker +
  Explorer UI). Runs in **demo mode** (`OSB_EXPLORER_DEMO=true`): the Explorer's connection
  inputs are grayed out ("Not changeable in the live demo") and a reset countdown shows in
  the top bar. Only the Explorer port (5400) is exposed, on loopback, for nginx.
- **seeder** — `mauritsarissen/openservicebus-demo-seeder:latest`. Creates the demo topology
  (2 queues + 1 topic with 3 subscriptions, two SQL-filtered), drives fluctuating load that
  never exceeds ~450 active messages (sending, completing, dead-lettering, DLQ cleanup,
  deferring, abandoning), and wipes + recreates everything on each 30-minute boundary.

The reset cadence (`*_RESET_INTERVAL_SECONDS`) is shared by both — they align to wall-clock
boundaries (:00 / :30 UTC), so the countdown and the actual reset agree with no coordination.

## Deploy

CI does this automatically (`.github/workflows/demo-deploy.yml`) on every release, on
changes here, or on manual dispatch: it builds/pushes the seeder image, rsyncs this folder
to `/opt/openservicebus-demo/` on the VPS, and runs `docker compose pull && up -d`.

## One-time VPS setup (manual)

1. **DNS**: add an A record `demo.openservicebus.net` → the VPS IP.
2. **Docker**: ensure Docker + the compose plugin are installed on the VPS.
3. **Reverse proxy** ([`nginx-demo.conf`](nginx-demo.conf)):
   ```bash
   sudo cp /opt/openservicebus-demo/nginx-demo.conf /etc/nginx/sites-available/demo.openservicebus.net
   sudo ln -s /etc/nginx/sites-available/demo.openservicebus.net /etc/nginx/sites-enabled/
   sudo certbot --nginx -d demo.openservicebus.net   # TLS
   sudo nginx -t && sudo systemctl reload nginx
   ```
4. First deploy: trigger the **Deploy demo** workflow (or push a change here).

Required repo secrets (already present for the website/release workflows): `DOCKERHUB_USERNAME`,
`DOCKERHUB_TOKEN`, `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY`.

## Run it locally

```bash
docker compose -f deploy/demo/docker-compose.yml up -d   # from the repo root
# Explorer: http://localhost:5400  (in demo mode)
```
