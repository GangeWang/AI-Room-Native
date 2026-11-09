# Downloads the unmodified AIROOM artwork from a pinned upstream commit.
# Every file is verified with `git hash-object` before it is accepted —
# the same check as the macOS fetch-original-assets.sh.
# Requires git and curl (or Invoke-WebRequest fallback) on PATH.
$ErrorActionPreference = 'Stop'

$root = 'https://raw.githubusercontent.com/tonyAmadeus/offAice/c6e2b59114763d83c93edeb2758a6cbe2ffbb952'
$dest = Join-Path $PSScriptRoot '..\AIROOMApp\Assets'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$assets = @(
    @{ remote = 'build/airoom.png';                          name = 'airoom.png';   sha = '0f068854e05426b1ee3d9352da42b0ea0e7e74a9' },
    @{ remote = 'src/renderer/assets/members/claudia.webp';  name = 'claudia.webp'; sha = 'ae0a5afde2a86fa48600689e0c9339ab19b644ad' },
    @{ remote = 'src/renderer/assets/members/gptia.webp';    name = 'gptia.webp';   sha = '81ffacdf714ea3a3c55a60410594b56a4da603e9' },
    @{ remote = 'src/renderer/assets/members/ds.webp';       name = 'ds.webp';      sha = 'baa3f7f51f0330d6ae2cb7d4803c70f07e4f67dc' },
    @{ remote = 'src/renderer/assets/members/gemini.webp';   name = 'gemini.webp';  sha = '756846a790d447377305da53dee8ed883f3ff2f3' },
    @{ remote = 'KIMI.webp';                                 name = 'kimi.webp';    sha = '78cd92bd91738268050cdcd275647aead5c029dd' },
    @{ remote = 'GLM.jpg';                                   name = 'glm.jpg';      sha = '3fb70f5066fe3e785948f3fb9463dcf807f4e2b6' },
    @{ remote = 'src/renderer/assets/rooms/morning.webp';    name = 'morning.webp'; sha = '7e0674b1d49a440700348eb93fb8bbe2b8b8aae5' },
    @{ remote = 'src/renderer/assets/rooms/afternoon.webp';  name = 'afternoon.webp'; sha = 'eeac77f36360f8c0a009a702a84e9c2f316c7029' },
    @{ remote = 'src/renderer/assets/rooms/evening.webp';    name = 'evening.webp'; sha = 'e5502e1a368fe606a7a4676ca460937588360e56' },
    @{ remote = 'src/renderer/assets/rooms/night.webp';      name = 'night.webp';   sha = '9eba00c742b9be383f5d46e4289ab0d8cbbfbb56' }
)

function HashOf([string]$path) {
    git hash-object $path
}

foreach ($asset in $assets) {
    $output = Join-Path $dest $asset.name
    if ((Test-Path $output) -and ((HashOf $output) -eq $asset.sha)) {
        Write-Host "Verified local asset: $($asset.name)"
        continue
    }
    $temp = Join-Path $dest (".asset-" + [guid]::NewGuid().ToString('N'))
    curl.exe --proto '=https' --tlsv1.2 --fail --location --silent --show-error --max-time 90 `
        "$root/$($asset.remote)" -o $temp
    if (-not (Test-Path $temp)) {
        Remove-Item -Force $temp -ErrorAction SilentlyContinue
        Write-Error "Failed to download $($asset.name); the app falls back to initial-letter avatars."
    }
    $actual = HashOf $temp
    if ($actual -ne $asset.sha) {
        Remove-Item -Force $temp
        throw "Asset SHA mismatch for $($asset.name). Expected $($asset.sha), got $actual"
    }
    Move-Item -Force $temp $output
    Write-Host "Downloaded and verified: $($asset.name)"
}

Write-Host 'Done. Rebuild the app to bundle these resources.'
