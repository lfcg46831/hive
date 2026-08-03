#!/bin/sh
# Materializes the console's runtime configuration before nginx starts.
#
# The values arrive as environment variables and are written into the served
# `config.js`, so no credential is ever baked into an image layer. The script
# fails the container start on missing or unusable input rather than serving a
# console that renders a configuration error to whoever opens it.

set -eu

target=/usr/share/nginx/html/config.js

api_base_url="${HIVE_CONSOLE_API_BASE_URL:-}"
organization_id="${HIVE_CONSOLE_ORGANIZATION_ID:-}"
token="${HIVE_CONSOLE_TOKEN:-}"
poll_interval_ms="${HIVE_CONSOLE_POLL_INTERVAL_MS:-5000}"

if [ -z "$organization_id" ] || [ -z "$token" ]; then
    echo "hive-console: HIVE_CONSOLE_ORGANIZATION_ID and HIVE_CONSOLE_TOKEN are required." >&2
    exit 1
fi

# The values are interpolated into JavaScript string literals. Rather than
# escape them, reject the characters that could break out: a legitimate
# organization id or bearer token contains none of them.
for value in "$api_base_url" "$organization_id" "$token"; do
    case "$value" in
        *\'* | *\\* | *'
'*)
            echo "hive-console: quotes, backslashes and newlines are not allowed in configuration values." >&2
            exit 1
            ;;
    esac
done

case "$poll_interval_ms" in
    '' | *[!0-9]*)
        echo "hive-console: HIVE_CONSOLE_POLL_INTERVAL_MS must be a whole number of milliseconds." >&2
        exit 1
        ;;
esac

cat > "$target" <<EOF
// Generated at container start by 40-hive-console-config.sh. Do not edit.
window.__HIVE_CONSOLE_CONFIG__ = {
  apiBaseUrl: '${api_base_url}',
  organizationId: '${organization_id}',
  token: '${token}',
  pollIntervalMs: ${poll_interval_ms},
};
EOF

echo "hive-console: runtime configuration written for organization ${organization_id}."
