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
# LegACEy plugins: Plugins\<Name>\<Name>.dll beside the client. Each folder ships only its own DLL.
$pluginRoot = Join-Path $BuildOutput 'Plugins'
if (!(Test-Path (Join-Path $pluginRoot 'Paperdoll\Paperdoll.dll'))) { throw 'Missing plugin assembly: Plugins\Paperdoll\Paperdoll.dll' }
if (!(Test-Path (Join-Path $pluginRoot 'Vault\Vault.dll'))) { throw 'Missing plugin assembly: Plugins\Vault\Vault.dll' }
if (!(Test-Path (Join-Path $pluginRoot 'Inventory\Inventory.dll'))) { throw 'Missing plugin assembly: Plugins\Inventory\Inventory.dll' }
$pluginDestination = Join-Path $runtime 'Plugins'
New-Item -ItemType Directory -Path $pluginDestination | Out-Null
foreach ($folder in Get-ChildItem $pluginRoot -Directory) {
    $dlls = @(Get-ChildItem $folder.FullName -File -Filter '*.dll')
    if ($dlls.Count -ne 1 -or $dlls[0].Name -ne "$($folder.Name).dll") { throw "Plugin folder $($folder.Name) must hold only $($folder.Name).dll" }
    $target = Join-Path $pluginDestination $folder.Name
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item $dlls[0].FullName $target
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
