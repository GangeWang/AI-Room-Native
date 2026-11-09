# Builds and (optionally) publishes the Windows app.
# Usage:
#   powershell -File Scripts\build.ps1            # build + tests
#   powershell -File Scripts\build.ps1 -Publish   # also publish win-x64 into dist\
param(
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$dotnet = 'dotnet'
$repo = Join-Path $PSScriptRoot '..'

Push-Location $repo
try {
    & $dotnet build AIROOMNative.slnx
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    & $dotnet test AIROOMCore.Tests/AIROOMCore.Tests.csproj
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

    if ($Publish) {
        New-Item -ItemType Directory -Force -Path dist | Out-Null
        & $dotnet publish AIROOMApp/AIROOMApp.csproj -c Release -r win-x64 --self-contained `
            -p:Platform=x64 -o dist/AIROOM
        if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
        Write-Host "Published to windows\dist\AIROOM — run dist\AIROOM\AIROOM.exe"
    }
}
finally {
    Pop-Location
}
