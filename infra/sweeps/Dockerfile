# The regionizer simulator CLI as a container image, so a sweep runs as a Kubernetes Job (spec 014, FR-032;
# issue #62). Build from the repository root:
#
#   docker build -f infra/sweeps/Dockerfile -t mmo-engine/regionizer-sim:local .
#
# The image runs job-entrypoint.sh, which maps an indexed Job's completion index to `sweep --shard k/n`.
# infra/sweeps/k8s-sweep.sh builds it, loads it into the local cluster and runs the Job.
# The SDK matches global.json (its 10.0.1xx feature band; it only compiles). The runtime is a 10.0 patch release,
# as setup-dotnet's latestPatch roll-forward gives the plain-process runs. Both are pinned by digest, like every
# other dependency here; Dependabot (.github/dependabot.yml, docker) proposes the newer patches.
FROM mcr.microsoft.com/dotnet/sdk:10.0.100@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
# The project files first, so a source edit reuses the restored packages (the restore layer stays cached).
COPY src/MmoEngine.Regionizer/MmoEngine.Regionizer.csproj src/MmoEngine.Regionizer/
COPY src/MmoEngine.Maps/MmoEngine.Maps.csproj src/MmoEngine.Maps/
COPY src/MmoEngine.Regionizer.Simulator/MmoEngine.Regionizer.Simulator.csproj src/MmoEngine.Regionizer.Simulator/
COPY src/MmoEngine.Regionizer.Simulator.Cli/MmoEngine.Regionizer.Simulator.Cli.csproj src/MmoEngine.Regionizer.Simulator.Cli/
RUN dotnet restore src/MmoEngine.Regionizer.Simulator.Cli
COPY simulator/scenarios/ simulator/scenarios/
COPY src/MmoEngine.Regionizer/ src/MmoEngine.Regionizer/
COPY src/MmoEngine.Maps/ src/MmoEngine.Maps/
COPY src/MmoEngine.Regionizer.Simulator/ src/MmoEngine.Regionizer.Simulator/
COPY src/MmoEngine.Regionizer.Simulator.Cli/ src/MmoEngine.Regionizer.Simulator.Cli/
RUN dotnet publish src/MmoEngine.Regionizer.Simulator.Cli -c Release -nologo --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15@sha256:80a51954ac70925e5783cb1ed35129a4243dcb1cddef20066b67346e17db284d
WORKDIR /app
COPY --from=build /app/ /app/
COPY infra/sweeps/job-entrypoint.sh /app/job-entrypoint.sh
# Map files named by scenarios (simulator/maps/stacked-base.json for StackedAreas and Soak) resolve upward from
# the working directory, /app.
COPY simulator/maps/ /app/simulator/maps/
# The image has no repository, so the revision it was built from is recorded in manifest.json's gitSha
# (k8s-sweep.sh passes `git rev-parse HEAD`).
ARG GIT_SHA=unknown
ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true MMO_GIT_SHA=${GIT_SHA}
ENTRYPOINT ["/app/job-entrypoint.sh"]
