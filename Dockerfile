# syntax=docker/dockerfile:1

# BuildCv.Api.
#
# Two stages so the runtime image carries the published output and no SDK. Restore is a separate
# layer from build so a source edit does not re-download every package.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# global.json pins the SDK feature band, so it has to be present before any dotnet command runs or
# the wrong one is selected silently.
COPY global.json ./
COPY BuildCv.slnx ./
# The dotnet-ef version is pinned here, and the migrations stage below restores it rather than
# installing whatever `dotnet tool install` resolves that day.
COPY .config/dotnet-tools.json .config/
COPY src/BuildCv.Domain/*.csproj src/BuildCv.Domain/
COPY src/BuildCv.Application/*.csproj src/BuildCv.Application/
COPY src/BuildCv.Infrastructure/*.csproj src/BuildCv.Infrastructure/
COPY src/BuildCv.Api/*.csproj src/BuildCv.Api/
RUN dotnet restore src/BuildCv.Api/BuildCv.Api.csproj

COPY src/ src/
RUN dotnet publish src/BuildCv.Api/BuildCv.Api.csproj -c Release -o /app --no-restore

# The schema, as a reviewable artifact.
#
# --idempotent guards every migration with a check against __EFMigrationsHistory, so applying it twice
# is a no-op and the one-shot migrator can run on every `up`. Per CLAUDE.md, `dotnet ef` takes
# BuildCv.Infrastructure as BOTH project and startup project -- the design-time factory lives there and
# the Api project does not reference EntityFrameworkCore.Design.
FROM build AS migrations
RUN dotnet tool restore \
  && mkdir -p /migrations \
  && dotnet tool run dotnet-ef migrations script \
       --idempotent \
       --project src/BuildCv.Infrastructure \
       --startup-project src/BuildCv.Infrastructure \
       --output /migrations/BuildCv.sql

# The same SQL Server image the compose file runs, reused for its sqlcmd rather than pulling a second
# base: it is already on disk, and /opt/mssql-tools18/bin/sqlcmd is the exact binary the server's own
# healthcheck uses. This stage never starts a database -- its entrypoint is the migration script.
FROM mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04 AS migrator
USER root
# The directory is created HERE rather than implicitly by COPY. That began as a --chmod problem -- the
# flag applied to the parent COPY created, and a directory at 644 has no execute bit, so mssql could not
# traverse it and sqlcmd reported '/migrations/BuildCv.sql': Invalid filename, which reads like a
# missing file. The explicit mkdir is kept now that --chmod is gone: it states the directory's mode
# instead of inheriting whatever COPY would have left.
RUN mkdir -p /migrations
# A SEPARATE RUN chmod, NOT `COPY --chmod`, and the difference is portability rather than taste:
# --chmod requires BuildKit, and `az acr build` uses the classic builder, so the image built locally
# and failed in Azure with "the --chmod option requires BuildKit". RUN chmod works on both.
#
# The mode still has to be stated. COPY preserves the builder's umask, and this container runs as
# mssql: a 640 script fails with "Error code 0x80070005" -- access denied, reported by sqlcmd as a
# problem opening the file rather than as a permission.
COPY --from=migrations /migrations/BuildCv.sql /migrations/BuildCv.sql
COPY docker/migrate.sh /usr/local/bin/migrate.sh
RUN chmod 644 /migrations/BuildCv.sql && chmod 755 /usr/local/bin/migrate.sh
USER mssql
ENTRYPOINT ["/usr/local/bin/migrate.sh"]

# The backup sidecar. Same base as the migrator and for the same reason -- it needs sqlcmd, and this
# image is already on disk. Separate stage rather than a second entrypoint on the migrator, because one
# runs once and exits while this one runs forever, and conflating them would make
# `service_completed_successfully` wait on a loop that never completes.
FROM mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04 AS backup
USER root
COPY docker/backup.sh /usr/local/bin/backup.sh
RUN chmod 755 /usr/local/bin/backup.sh
USER mssql
ENTRYPOINT ["/usr/local/bin/backup.sh"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl, for the health probe below and nothing else. The aspnet image ships no HTTP client, and the
# app exposes no self-check flag, so a HEALTHCHECK has to speak HTTP from inside the container.
USER root
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl \
  && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
# Production by default, which is also what refuses the in-memory store: Persistence:Provider cannot
# select it here, so a container can never come up serving accounts out of a dictionary.
ENV ASPNETCORE_ENVIRONMENT=Production

# A SEPARATE RUN chmod rather than `COPY --chmod`: the latter needs BuildKit, and `az acr build` uses
# the classic builder -- the image built here and failed in Azure. `a+rX` rather than 755 so directories
# get traversal and files keep their execute bit only where they had one.
#
# The mode is stated at all because COPY PRESERVES THE SOURCE MODE, and the source mode is the umask.
# On a machine with umask 077 the published files arrive 640 -- readable by root, and by nobody else --
# and the container then drops to $APP_UID and cannot read its own appsettings.json:
#
#   Unhandled exception. System.UnauthorizedAccessException:
#     Access to the path '/app/appsettings.json' is denied.
#
# Thrown from WebApplication.CreateBuilder before a single line of this codebase runs, and surfacing to
# `docker compose ps` as "Restarting (139)", which reads like a segfault rather than a permission.
#
# The point is that this is INVISIBLE on a normal workstation: umask 022 publishes 644 and the image
# works. It fails only for whoever builds with a restrictive umask -- so it is exactly the class of bug
# that ships green and breaks on somebody else's machine. Stating the mode makes the image independent
# of who built it.
COPY --from=build /app ./
RUN chmod -R a+rX /app

# The aspnet image ships this user. The app writes nothing to disk, so it needs nothing it owns.
USER $APP_UID

EXPOSE 8080

# LIVENESS, not readiness, and the distinction is the app's own: /health/live touches nothing outside
# the process, while /health/ready opens a database connection. A failed Docker healthcheck can
# restart the container, so probing readiness here would roll-restart the API the moment SQL Server
# hiccuped — at the moment it can least afford a reconnection stampede. Readiness is for whatever
# decides where to send traffic, and it is exposed for that.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "BuildCv.Api.dll"]
