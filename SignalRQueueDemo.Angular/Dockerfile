# One Dockerfile builds all three Angular apps (public-checkin, internal-queue, queue-display), parameterized by
# the APP_NAME build arg. A separate Dockerfile per app would just be this file copy-pasted three times with one
# line changed — every app in this workspace shares the exact same build (npm ci -> build shared -> build the
# app) and serve (nginx + the same entrypoint/conf) recipe, so one file with an ARG is less to keep in sync than
# three near-duplicates. SignalRQueueDemo.AppHost/AppHost.cs passes APP_NAME via WithBuildArg for each of the
# three container resources it declares.
#
# BUILD CONTEXT: this Dockerfile expects its context to be this SignalRQueueDemo.Angular/ folder (the whole CLI
# workspace), not an individual projects/<app> folder. Every app depends on the "shared" library (see
# tsconfig.json's `"shared": ["./dist/shared"]` path mapping) plus the workspace-root package.json/angular.json,
# so the build needs all of projects/, not just one app's subtree. AppHost.cs sets contextPath to this folder and
# dockerfilePath to this file explicitly (both live here, so no path argument is actually needed beyond the
# default, but it's spelled out at the call site for clarity).

# --- Stage 1: build -----------------------------------------------------------------------------------------
# node:22-slim (not full node:22 or node:22-alpine): matches the workspace's pinned Node 22 engine
# (package.json/angular.json target Angular 22, which requires Node >=20.19 || >=22.12) with a materially smaller
# image than the full Debian-based node:22 tag, while avoiding node:22-alpine's musl-libc — some npm postinstall
# scripts in the Angular/ng-packagr toolchain assume glibc and are flaky on musl. This stage's size doesn't ship
# in the final image at all (multi-stage build discards it), so the choice here only affects build time, not the
# deliverable's footprint — see the nginx stage below for the choice that actually matters for image size.
FROM node:22-slim AS build
WORKDIR /workspace

# Dependencies copied and installed before the rest of the source so Docker's layer cache can skip `npm ci`
# (the slowest step) on a rebuild that only changed app source, not package.json/package-lock.json.
COPY package.json package-lock.json ./
RUN npm ci

# Now bring in the rest of the workspace (projects/, angular.json, tsconfig.json, etc.) — see .dockerignore for
# what's deliberately excluded from this COPY (node_modules, dist, .angular cache).
COPY . .

# Which of the three apps this image serves. No default: an image built without APP_NAME would silently build
# nothing useful, and this Dockerfile is never meant to be built without AppHost.cs supplying it.
ARG APP_NAME

# "shared" must build first — every app's tsconfig path mapping resolves the "shared" import to ./dist/shared,
# not to projects/shared/src directly, so an app build started before this exists fails to resolve that import.
# Both builds default to the "production" configuration already defined per-project in angular.json (optimized,
# hashed filenames) — the same configuration `npm run build` (no --configuration flag) uses locally.
RUN npx ng build shared && npx ng build "$APP_NAME"

# --- Stage 2: serve -------------------------------------------------------------------------------------------
# nginx:alpine: this is the image that actually ships, so its size is the one that matters for the
# "record observed image sizes" acceptance criterion below (see README.md) — alpine's musl-libc concerns from the
# build stage don't apply here because nginx itself has no Node/npm postinstall scripts to trip over it.
FROM nginx:alpine
ARG APP_NAME

# nginx must know which of the three apps' output to serve and how to route it (SPA fallback + config.json
# caching) — see docker/nginx.conf's comments for why each directive is there.
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf

# The official nginx:alpine image runs every executable *.sh script under /docker-entrypoint.d/ (in name order)
# before starting nginx itself -- a documented extension point, not a custom ENTRYPOINT override, so this stays
# compatible with anything else the base image's entrypoint already does. See the script's own comments for why
# this step exists at all (baking the API address at build time would break Aspire service discovery).
COPY docker/write-runtime-config.sh /docker-entrypoint.d/40-write-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/40-write-runtime-config.sh

# Angular's application builder emits to dist/<app>/browser/ (not dist/<app>/ directly) -- the extra "browser"
# segment is where @angular/build:application separates client output from its (unused here) server-rendering
# output. Copying that subfolder, not its parent, is what makes index.html land at nginx's document root.
COPY --from=build /workspace/dist/${APP_NAME}/browser /usr/share/nginx/html
