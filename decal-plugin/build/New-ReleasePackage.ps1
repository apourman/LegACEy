param(
    [Parameter(Mandatory)][string]$BuildOutput,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$Tag
)

$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^decal-plugin-v\d+\.\d+\.\d+$') { throw 'Invalid release tag' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$runtime = Join-Path $Destination 'runtime'
if (Test-Path $runtime) { throw 'Use a fresh packaging directory' }
New-Item -ItemType Directory -Path $runtime | Out-Null
Get-ChildItem $BuildOutput -File -Filter '*.dll' | Copy-Item -Destination $runtime
New-Item -ItemType Directory -Path (Join-Path $runtime 'x86') | Out-Null
foreach ($name in @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
    Copy-Item (Join-Path $BuildOutput "x86/$name") (Join-Path $runtime "x86/$name")
}
foreach ($name in @('LegACEy.Client.DecalPlugin.dll', 'LegACEy.Client.PanelHost.dll', 'LegACEy.Client.InputRouter.dll', 'LegACEy.Client.GameArt.dll', 'LegACEy.Client.Themes.dll', 'LegACEy.Client.Demo.dll')) {
    if (!(Test-Path (Join-Path $runtime $name))) { throw "Missing runtime assembly: $name" }
}
foreach ($name in @('Decal.Adapter.dll', 'Decal.Interop.Core.dll', 'Microsoft.DirectX.dll', 'Microsoft.DirectX.Direct3D.dll')) {
    if (Test-Path (Join-Path $runtime $name)) { throw "Build reference must not ship: $name" }
}
Copy-Item (Join-Path $PSScriptRoot '../../LICENSE') (Join-Path $runtime 'LICENSE')
Copy-Item (Join-Path $PSScriptRoot '../LegACEy.Client.Themes/Assets/LiberationFonts-LICENSE.txt') $runtime
$dll = Join-Path $Destination 'LegACEy.Client.DecalPlugin.dll'
Copy-Item (Join-Path $runtime 'LegACEy.Client.DecalPlugin.dll') $dll
$zip = Join-Path $Destination "LegACEy-$Tag-windows-x86.zip"
Compress-Archive -Path (Join-Path $runtime '*') -DestinationPath $zip
$lines = @($dll, $zip) | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
}
[IO.File]::WriteAllText((Join-Path $Destination 'SHA256SUMS.txt'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
