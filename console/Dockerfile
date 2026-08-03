# Console image: builds the static bundle and serves it next to the public API.
#
# Two properties matter here. The image holds no configuration and no secret:
# `config.js` is written at container start from the environment (see
# docker/40-hive-console-config.sh), so the same image runs against any host and
# a rotated credential is a restart rather than a rebuild. And nginx serves the
# bundle and proxies `/api/v1` from the same origin, so the browser makes no
# cross-origin request — which is what lets the console work against a backend
# that configures no CORS policy.

FROM node:22-alpine AS build
WORKDIR /src

# Manifests first: dependency installation is cached until they change.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:1.27-alpine AS runtime

COPY docker/nginx.conf /etc/nginx/conf.d/default.conf

# The nginx image runs every script in this directory before starting the
# server, which is where the runtime configuration is materialized.
COPY docker/40-hive-console-config.sh /docker-entrypoint.d/40-hive-console-config.sh
RUN chmod +x /docker-entrypoint.d/40-hive-console-config.sh

COPY --from=build /src/dist /usr/share/nginx/html

EXPOSE 80
