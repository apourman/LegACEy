param(
    [string]$Version = '0.0.0',
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'legacey-plugin-release'),
    # The release workflow skips the tests: they already passed on the promote pull request.
    [switch]$SkipTests
)

# Everything the plugin release runs, in the same configuration: the release workflow calls it, the promote pull request into
# master runs it on windows-2022, and it runs on any Windows machine with .NET 10, Python and Inno Setup 6.
# The package (stub, plugins, runtime, setup.exe, SHA256SUMS.txt) lands in <WorkDirectory>\plugin-package.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$WorkDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
$output = Join-Path $WorkDirectory 'plugin-output'
$destination = Join-Path $WorkDirectory 'plugin-package'
# Only this script's own output is cleared; the downloaded build references stay cached between runs.
foreach ($folder in $output, $destination) { if (Test-Path $folder) { Remove-Item $folder -Recurse -Force } }
Push-Location (Join-Path $PSScriptRoot '../..')
try {
    $tag = "decal-plugin-v$Version"

    if (!$SkipTests) {
        python -m unittest discover -s decal-plugin/build -p 'test_*.py'
        dotnet test decal-plugin/LegACEy.Client.Tests/LegACEy.Client.Tests.csproj --configuration Release
    }

    ./decal-plugin/build/Get-BuildReferences.ps1 -Destination (Join-Path $WorkDirectory 'plugin-build-references')
    $references = Join-Path $WorkDirectory 'plugin-build-references/references'
    dotnet build decal-plugin/LegACEy.Client.DecalPlugin/LegACEy.Client.DecalPlugin.csproj `
        --configuration Release --output $output `
        "-p:Version=$Version" "-p:DecalInstallDir=$references" "-p:DecalInteropDir=$references"
    dotnet publish decal-plugin/LegACEy.Client/LegACEy.Client.csproj `
        --configuration Release --runtime win-x86 --self-contained `
        --output (Join-Path $output 'runtime') "-p:Version=$Version"

    if (!$SkipTests) { dotnet run --project decal-plugin/LegACEy.Client.HookSmoke --configuration Release }

    ./decal-plugin/build/New-ReleasePackage.ps1 -BuildOutput $output -Destination $destination -Tag $tag

    # Inno Setup 6 (preinstalled on the runner image) builds setup.exe from the package's files.
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source ?? "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (!(Test-Path $iscc)) { throw 'Inno Setup 6 is missing: no iscc on PATH or in Program Files (x86)' }
    & $iscc "/DPackageDir=$destination\package" "/DAppVersion=$Version" "/O$destination" decal-plugin\build\LegACEy-decal-plugin.iss
    $setup = "LegACEy-$tag-setup.exe"
    $line = "{0}  {1}`n" -f (Get-FileHash (Join-Path $destination $setup) -Algorithm SHA256).Hash.ToLowerInvariant(), $setup
    [IO.File]::AppendAllText((Join-Path $destination 'SHA256SUMS.txt'), $line)
    Write-Output "Release package built in $destination"
}
finally { Pop-Location }
