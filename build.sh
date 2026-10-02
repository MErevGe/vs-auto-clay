#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$project_dir"
if [[ -x "$project_dir/.tools/dotnet/dotnet" ]]; then
    export PATH="$project_dir/.tools/dotnet:$PATH"
fi
dotnet restore AutoClay/AutoClay.csproj --locked-mode
dotnet restore CakeBuild/CakeBuild.csproj --locked-mode
dotnet run --project CakeBuild/CakeBuild.csproj --no-restore -- "$@"
