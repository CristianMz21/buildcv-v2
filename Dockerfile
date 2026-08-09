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
COPY src/BuildCv.Domain/*.csproj src/BuildCv.Domain/
COPY src/BuildCv.Application/*.csproj src/BuildCv.Application/
COPY src/BuildCv.Infrastructure/*.csproj src/BuildCv.Infrastructure/
COPY src/BuildCv.Api/*.csproj src/BuildCv.Api/
RUN dotnet restore src/BuildCv.Api/BuildCv.Api.csproj

COPY src/ src/
RUN dotnet publish src/BuildCv.Api/BuildCv.Api.csproj -c Release -o /app --no-restore

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

COPY --from=build /app ./

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
