#!/bin/sh
# Runs automatically at container start: the official nginx:alpine image executes every executable *.sh script
# under /docker-entrypoint.d/ (sorted by name) before nginx begins serving.
#
# WHY THIS EXISTS (read before "simplifying" it away): the Angular apps in this workspace resolve their API
# address from /config.json, fetched at runtime -- see RuntimeConfigService (projects/shared/src/lib/config) --
# specifically *because* baking it in at `docker build` time can't work here. Aspire only assigns the API's
# externally-reachable address (host + port) when `aspire run` starts the AppHost, which is long after this
# image was built. A build-time value would freeze in an address that doesn't exist yet, making the image
# unusable in any environment but the one it happened to be built for. Overwriting config.json here, from an
# environment variable Aspire injects at container start (API_BASE_URL -- see AppHost.cs's WithEnvironment call
# for each Angular container resource), keeps one image deployable anywhere; only this one file changes per run.
set -eu

if [ -z "${API_BASE_URL:-}" ]; then
    echo "write-runtime-config.sh: API_BASE_URL is not set -- refusing to start with a stale/placeholder config.json." >&2
    exit 1
fi

# Hand-built JSON, not a templating tool: RuntimeConfig (runtime-config.ts) is a tiny fixed shape, and every value
# here is an Aspire-supplied endpoint URL (scheme://host:port), never client input, so there's no untrusted content
# needing escaping. PUBLIC_CHECKIN_URL is optional — only queue-display's container is given it (for its check-in
# QR/link); when it's unset the field is omitted entirely rather than written as an empty/"null" string.
if [ -n "${PUBLIC_CHECKIN_URL:-}" ]; then
    cat > /usr/share/nginx/html/config.json <<EOF
{"apiBaseUrl":"${API_BASE_URL}","publicCheckinUrl":"${PUBLIC_CHECKIN_URL}"}
EOF
    echo "write-runtime-config.sh: wrote config.json with apiBaseUrl=${API_BASE_URL} publicCheckinUrl=${PUBLIC_CHECKIN_URL}"
else
    cat > /usr/share/nginx/html/config.json <<EOF
{"apiBaseUrl":"${API_BASE_URL}"}
EOF
    echo "write-runtime-config.sh: wrote config.json with apiBaseUrl=${API_BASE_URL}"
fi
