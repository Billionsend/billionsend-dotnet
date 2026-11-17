#!/usr/bin/env bash
# Runs a dotnet command in the pinned .NET SDK image with rootless podman, at low
# priority and capped CPU and memory, so a build never competes with what else
# the machine runs. The NuGet cache stays in the repository (.nuget-cache/,
# ignored by git).
#
#   scripts/dotnet.sh test
#   scripts/dotnet.sh pack src/BillionSend -c Release -o artifacts
set -euo pipefail

IMAGE="mcr.microsoft.com/dotnet/sdk:8.0.427"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$ROOT/.nuget-cache" "$ROOT/.dotnet-home"

exec systemd-run --user --scope --quiet -p CPUQuota=800% -p MemoryMax=16G \
  nice -n 10 \
  podman run --rm \
    -v "$ROOT:/src:Z" \
    -v "$ROOT/.nuget-cache:/root/.nuget/packages:Z" \
    -v "$ROOT/.dotnet-home:/root/.dotnet:Z" \
    -w /src \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    "$IMAGE" dotnet "$@"
