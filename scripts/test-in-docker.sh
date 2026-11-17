#!/usr/bin/env sh
# Runs the whole test suite inside the .NET SDK container, against the host's Docker daemon.
# Useful when the host cannot run the tests directly (no .NET SDK, or a policy that blocks
# locally built assemblies). Testcontainers starts PostgreSQL as a sibling container.
#
#   sh scripts/test-in-docker.sh
set -eu

# On Git Bash for Windows, hand Docker a Windows path and stop MSYS from rewriting arguments.
export MSYS_NO_PATHCONV=1
repo_root=$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))

docker run --rm \
  -v "$repo_root:/repo:ro" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v carteiro-nuget-cache:/root/.nuget/packages \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  --add-host host.docker.internal:host-gateway \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  sh -c 'mkdir /work && cd /repo && tar --exclude=./.git --exclude="*/bin" --exclude="*/obj" -cf - . | tar -xf - -C /work \
         && cd /work && dotnet build -warnaserror && dotnet test --no-build "$@"' -- "$@"
