# Updates a local plugin checkout in place: pulls (fast-forward only), builds the stub with its plugins, and publishes the client into
# runtime\ beside it, the layout Decal is registered against. Close the game first; it holds these files open.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$output = Join-Path $root 'LegACEy.Client.DecalPlugin\bin\Release\net48'

function Invoke-Step([string]$name, [scriptblock]$step) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $step
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)" }
}

if (Get-Process acclient -ErrorAction SilentlyContinue) { throw 'The game is running: close it first, it holds the plugin files open.' }

Invoke-Step 'Pull' { git -C $root pull --ff-only }
Invoke-Step 'Build the stub and plugins' {
    dotnet build (Join-Path $root 'LegACEy.Client.DecalPlugin\LegACEy.Client.DecalPlugin.csproj') --configuration Release
}
Invoke-Step 'Publish the client into runtime\' {
    dotnet publish (Join-Path $root 'LegACEy.Client\LegACEy.Client.csproj') --configuration Release --runtime win-x86 --self-contained `
        --output (Join-Path $output 'runtime')
}
Write-Host "Updated to $(git -C $root log --oneline -1). Start the game." -ForegroundColor Green
