param(
    [Parameter(Mandatory)][string]$BuildOutput,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$Tag
)

# BuildOutput is the stub's build output with the published client in its runtime\ folder.
# The zip holds the stub (and GameArt, which it uses), the updater, Plugins\, runtime\ and the license notices.
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^decal-plugin-v\d+\.\d+\.\d+$') { throw 'Invalid release tag' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$package = Join-Path $Destination 'package'
if (Test-Path $package) { throw 'Use a fresh packaging directory' }
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item (Join-Path $BuildOutput 'LegACEy.Client.DecalPlugin.dll'), (Join-Path $BuildOutput 'LegACEy.Client.GameArt.dll') $package
# Fails the package when the stub build left no updater beside it.
Copy-Item (Join-Path $BuildOutput 'LegACEy.Updater.exe') $package
$runtime = Join-Path $BuildOutput 'runtime'
foreach ($name in @('coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll', 'netstandard.dll', 'LegACEy.Client.dll', 'LegACEy.Client.PanelHost.dll', 'LegACEy.Client.InputRouter.dll', 'LegACEy.Client.GameArt.dll', 'LegACEy.Client.Themes.dll', 'LegACEy.Client.Demo.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
    if (!(Test-Path (Join-Path $runtime $name))) { throw "Incomplete runtime: runtime\$name is missing. Publish the client into runtime\ first." }
}
# The game is x86: coreclr.dll's PE machine field must be 0x14C.
$coreclr = [IO.File]::ReadAllBytes((Join-Path $runtime 'coreclr.dll'))
if ([BitConverter]::ToUInt16($coreclr, [BitConverter]::ToInt32($coreclr, 0x3C) + 4) -ne 0x14C) { throw 'runtime\coreclr.dll is not x86: publish with -r win-x86.' }
Copy-Item $runtime $package -Recurse
# LegACEy plugins: Plugins\<Name>\<Name>.dll beside the stub. Each folder ships only its own DLL.
$pluginRoot = Join-Path $BuildOutput 'Plugins'
foreach ($name in @('Vault', 'Inventory')) {
    if (!(Test-Path (Join-Path $pluginRoot "$name\$name.dll"))) { throw "Missing plugin assembly: Plugins\$name\$name.dll" }
}
$pluginDestination = Join-Path $package 'Plugins'
New-Item -ItemType Directory -Path $pluginDestination | Out-Null
foreach ($folder in Get-ChildItem $pluginRoot -Directory) {
    $dlls = @(Get-ChildItem $folder.FullName -File -Filter '*.dll')
    if ($dlls.Count -ne 1 -or $dlls[0].Name -ne "$($folder.Name).dll") { throw "Plugin folder $($folder.Name) must hold only $($folder.Name).dll" }
    $target = Join-Path $pluginDestination $folder.Name
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item $dlls[0].FullName $target
}
$references = @(Get-ChildItem $package -Recurse -File | Where-Object { $_.Name -like 'Decal*.dll' -or $_.Name -like 'Microsoft.DirectX*.dll' })
if ($references.Count -gt 0) { throw "Build reference must not ship: $($references[0].Name)" }
Copy-Item (Join-Path $PSScriptRoot '../../LICENSE') (Join-Path $package 'LICENSE')
Copy-Item (Join-Path $PSScriptRoot '../LegACEy.Client.Themes/Assets/LiberationFonts-LICENSE.txt') $package
$dll = Join-Path $Destination 'LegACEy.Client.DecalPlugin.dll'
Copy-Item (Join-Path $package 'LegACEy.Client.DecalPlugin.dll') $dll
$zip = Join-Path $Destination "LegACEy-$Tag-windows-x86.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
$lines = @($dll, $zip) | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
}
[IO.File]::WriteAllText((Join-Path $Destination 'SHA256SUMS.txt'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
