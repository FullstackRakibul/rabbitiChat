# Mostisko — a creation of Mostisko, the AI.
#
# One image that serves the Vue app AND the API on a single port.
#
#   stage 1 (web)     npm ci + vite build            → /web/dist
#   stage 2 (api)     dotnet publish + copy dist     → /app/publish (incl. wwwroot)
#   stage 3 (runtime) aspnet:8.0 runtime only        → the shipped image
#
# The runtime stage carries no SDK, no node, and no source — just the published
# output — which keeps the image small and means build-time tooling can never be
# part of the attack surface.
#
# NOTHING SECRET IS BAKED IN. There is no COPY of appsettings.*.json beyond what
# the repo already tracks, and every credential (Ollama URL, cloud API keys) is
# read from the environment at RUN time. See docker-compose.yml.
#
# SQLite lives on a MOUNTED VOLUME at /data, not in the image: the container's
# own filesystem is disposable, so a database written inside it would vanish on
# the next deploy. See ConnectionStrings__Default below.

# ──────────────────────────────────────────────────────────────
# Stage 1 — build the frontend
# ──────────────────────────────────────────────────────────────
FROM node:22-alpine AS web

WORKDIR /web

# Copy manifests first so `npm ci` is cached and only re-runs when deps change.
COPY frontend/package.json frontend/package-lock.json* ./
RUN npm ci

COPY frontend/ ./

# The SPA and the API share an origin in this image, so the default "/api" base
# URL is correct and no build-time API host is needed. A split deployment
# (frontend elsewhere) overrides this — see DEPLOY.md.
#   docker build --build-arg VITE_API_BASE_URL=https://api.example.com/api .
ARG VITE_API_BASE_URL=""
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL

RUN npm run build

# ──────────────────────────────────────────────────────────────
# Stage 2 — build and publish the API, with the SPA inside it
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api

WORKDIR /src

# Restore against the project file alone, so a source-only change does not
# invalidate the (slow) restore layer.
COPY backend/RabbitIC.Api.csproj ./
RUN dotnet restore RabbitIC.Api.csproj

COPY backend/ ./
# The Ministry of Truth's site text is embedded from the frontend's folder (RabbitIC.Api.csproj).
COPY frontend/src/truth/content/ /frontend/src/truth/content/

# A stray local database or build output in the build context must not ship.
RUN rm -rf bin obj Data/*.db Data/*.db-shm Data/*.db-wal \
           rabbitic.db rabbitic.db-shm rabbitic.db-wal .vs

RUN dotnet publish RabbitIC.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# The built SPA becomes the API's wwwroot: UseStaticFiles serves it and
# MapFallback returns index.html for client-side routes (/ielts, /5lakhs, …).
COPY --from=web /web/dist /app/publish/wwwroot

# ──────────────────────────────────────────────────────────────
# Stage 3 — runtime
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# The aspnet runtime image ships neither curl nor wget, and the healthcheck
# needs one of them. curl is added before dropping to the non-root user, and the
# apt lists are removed in the same layer so they do not persist in the image.
RUN apt-get update \
 && apt-get install --no-install-recommends -y curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=api /app/publish ./

# The database directory. Declared so a `docker run` with no -v still starts
# (anonymous volume) while compose/Fly mount a named one for real persistence.
RUN mkdir -p /data
VOLUME ["/data"]

# ---- Runtime configuration ----
# Double underscore is .NET's separator for nested config keys, so
# ConnectionStrings__Default overrides ConnectionStrings:Default from
# appsettings.json without the file being edited or rebuilt.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/data/rabbitic.db" \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# Runs as a non-root user (the aspnet image ships one) so a container escape
# does not start as root. /data must be writable by it — compose handles this.
USER $APP_UID

# /public/server/status needs no auth and touches no external service, so it
# reports "this process is serving HTTP" without going red when Ollama is down.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl --fail --silent http://localhost:8080/public/server/status || exit 1

ENTRYPOINT ["dotnet", "/app/RabbitIC.Api.dll"]
