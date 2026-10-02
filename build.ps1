$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    dotnet restore AutoClay/AutoClay.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet restore CakeBuild/CakeBuild.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project CakeBuild/CakeBuild.csproj --no-restore -- @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
