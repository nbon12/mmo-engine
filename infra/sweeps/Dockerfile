# The regionizer simulator CLI as a container image, so a sweep runs as a Kubernetes Job (spec 014, FR-032;
# issue #62). Build from the repository root:
#
#   docker build -f infra/sweeps/Dockerfile -t mmo-engine/regionizer-sim:local .
#
# The image runs job-entrypoint.sh, which maps an indexed Job's completion index to `sweep --shard k/n`.
# infra/sweeps/k8s-sweep.sh builds it, loads it into the local cluster and runs the Job.
# The SDK matches global.json (its 10.0.1xx feature band; it only compiles). The runtime takes the latest
# 10.0 patch, as setup-dotnet's latestPatch roll-forward does for the plain-process runs.
ARG SDK_VERSION=10.0.100
ARG RUNTIME_VERSION=10.0
FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY simulator/scenarios/ simulator/scenarios/
COPY src/ src/
RUN dotnet publish src/MmoEngine.Regionizer.Simulator.Cli -c Release -nologo -o /app

FROM mcr.microsoft.com/dotnet/runtime:${RUNTIME_VERSION}
WORKDIR /app
COPY --from=build /app/ /app/
COPY infra/sweeps/job-entrypoint.sh /app/job-entrypoint.sh
# The image has no repository, so the revision it was built from is recorded in manifest.json's gitSha
# (k8s-sweep.sh passes `git rev-parse HEAD`).
ARG GIT_SHA=unknown
ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true MMO_GIT_SHA=${GIT_SHA}
ENTRYPOINT ["/app/job-entrypoint.sh"]
