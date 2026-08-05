#!/bin/sh
# PR preview environments on the VPS. Invoked over SSH by .github/workflows/pr-preview.yml:
#
#   pr-preview.sh deploy <pr-number> <app-image> <seeder-image>
#   pr-preview.sh teardown <pr-number>
#
# Each PR gets one broker+Explorer container (demo mode, in-memory storage) plus a demo
# seeder sidecar sharing its network namespace, published on loopback port 20000+PR and
# fronted by a generated nginx server block for pr-<PR>.openservicebus.net. Deploys are
# idempotent: rerunning replaces the containers and rewrites the nginx conf in place.
#
# Expects on the VPS: docker, nginx with a conf.d include, and a Cloudflare origin
# certificate covering *.openservicebus.net at $CERT_PATH/$KEY_PATH. `deploy` also
# expects GHCR_USER and GHCR_TOKEN in the environment for the registry login.
set -eu

COMMAND="${1:?usage: pr-preview.sh deploy|teardown <pr-number> [app-image] [seeder-image]}"
PR="${2:?missing PR number}"
case "$PR" in *[!0-9]*) echo "PR number must be numeric, got '$PR'" >&2; exit 1;; esac

NAME="osb-pr-${PR}"
PORT=$((20000 + PR % 10000))
DOMAIN="pr-${PR}.openservicebus.net"
CONF="/etc/nginx/conf.d/${NAME}.conf"
CERT_PATH="${CERT_PATH:-/etc/ssl/cloudflare/openservicebus.net.pem}"
KEY_PATH="${KEY_PATH:-/etc/ssl/cloudflare/openservicebus.net.key}"
CONNECTION="Endpoint=sb://127.0.0.1:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true"

reload_nginx() {
    nginx -t
    if command -v systemctl >/dev/null 2>&1; then
        systemctl reload nginx
    else
        nginx -s reload
    fi
}

case "$COMMAND" in
deploy)
    APP_IMAGE="${3:?missing app image}"
    SEEDER_IMAGE="${4:?missing seeder image}"

    echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
    docker pull "$APP_IMAGE"
    docker pull "$SEEDER_IMAGE"

    docker rm -f "$NAME-seeder" "$NAME" 2>/dev/null || true

    docker run -d --name "$NAME" \
        --restart unless-stopped \
        --memory 768m --cpus 1.0 \
        -p "127.0.0.1:${PORT}:5400" \
        -e OPENSERVICEBUS__STORAGE__MODE=InMemory \
        -e OSB_EXPLORER_DEMO=true \
        -e OSB_EXPLORER_CONNECTION="$CONNECTION" \
        -e OSB_EXPLORER_MGMT_URL="http://127.0.0.1:5300" \
        -e OSB_EXPLORER_RESET_INTERVAL_SECONDS=1800 \
        "$APP_IMAGE"

    docker run -d --name "$NAME-seeder" \
        --restart unless-stopped \
        --memory 256m --cpus 0.5 \
        --network "container:${NAME}" \
        -e SEEDER_CONNECTION="$CONNECTION" \
        -e SEEDER_MGMT_URL="http://127.0.0.1:5300" \
        -e SEEDER_RESET_INTERVAL_SECONDS=1800 \
        "$SEEDER_IMAGE"

    # Default server for the *.openservicebus.net wildcard DNS record: any hostname
    # without its own server block (unknown subdomains, torn-down previews) is sent to
    # the website's 404 page instead of falling through to whichever server block nginx
    # loads first. 302, not 301: a future preview subdomain visited before its deploy
    # must not be permanently cached as a redirect by browsers.
    cat > /etc/nginx/conf.d/osb-preview-000-catchall.conf <<NGINX
server {
    listen 80 default_server;
    listen 443 ssl default_server;
    server_name _;

    ssl_certificate     ${CERT_PATH};
    ssl_certificate_key ${KEY_PATH};

    return 302 https://www.openservicebus.net/404;
}
NGINX

    cat > "$CONF" <<NGINX
server {
    listen 80;
    listen 443 ssl http2;
    server_name ${DOMAIN};

    ssl_certificate     ${CERT_PATH};
    ssl_certificate_key ${KEY_PATH};

    location / {
        proxy_pass http://127.0.0.1:${PORT};
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 90s;
    }
}
NGINX
    reload_nginx

    for _ in $(seq 1 30); do
        if wget -q -O /dev/null "http://127.0.0.1:${PORT}/" 2>/dev/null \
            || curl -sf -o /dev/null "http://127.0.0.1:${PORT}/" 2>/dev/null; then
            echo "preview ${DOMAIN} is up on port ${PORT}"
            exit 0
        fi
        sleep 2
    done
    echo "preview container never became healthy" >&2
    docker logs --tail 50 "$NAME" >&2 || true
    exit 1
    ;;

teardown)
    docker rm -f "$NAME-seeder" "$NAME" 2>/dev/null || true
    docker images --format '{{.Repository}}:{{.Tag}}' \
        | grep -E "openservicebus-preview(-seeder)?:pr-${PR}\$" \
        | xargs -r docker rmi 2>/dev/null || true
    if [ -f "$CONF" ]; then
        rm -f "$CONF"
        reload_nginx
    fi
    echo "preview ${DOMAIN} removed"
    ;;

*)
    echo "unknown command '$COMMAND'" >&2
    exit 1
    ;;
esac
