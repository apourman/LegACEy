param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

function Get-VerifiedDownload($Url, $Name, $Sha256) {
    $path = Join-Path $Destination $Name
    if (!(Test-Path $path)) {
        Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
    }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Checksum mismatch for $Name"
    }
    return $path
}

# Extract official redistributables into a temporary directory; do not install
# Decal or DirectX, register plugins, or redistribute these reference assemblies.
$msi = Get-VerifiedDownload 'https://www.decaldev.com/releases/2983/Decal.msi' 'Decal.msi' '101365ba4378be20d9ab57ba9f1c1deda5f93bb1b7bdb511da836c9a69a31f26'
$decal = Join-Path $Destination 'decal'
$process = Start-Process msiexec.exe -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$decal`" /l*v `"$Destination\decal-extract.log`"" -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Decal extraction failed: $($process.ExitCode)" }

$redist = Get-VerifiedDownload 'https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe' 'directx_Jun2010_redist.exe' '053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b'
$directx = Join-Path $Destination 'directx'
New-Item -ItemType Directory -Force -Path $directx | Out-Null
$cab = Join-Path $directx 'Apr2006_MDX1_x86.cab'
if (!(Test-Path $cab)) {
    $process = Start-Process $redist -ArgumentList "/Q /T:`"$directx`"" -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "DirectX extraction failed: $($process.ExitCode)" }
}
if (!(Test-Path $cab)) { throw 'Managed DirectX cabinet not found' }
$process = Start-Process "$env:SystemRoot\System32\expand.exe" -ArgumentList "`"$cab`" -F:* `"$directx`"" -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Managed DirectX cabinet extraction failed' }

$references = Join-Path $Destination 'references'
New-Item -ItemType Directory -Force -Path $references | Out-Null
foreach ($name in @('Decal.Adapter.dll', 'Decal.Interop.Core.dll', 'Microsoft.DirectX.dll', 'Microsoft.DirectX.Direct3D.dll')) {
    $referenceCopies = @(Get-ChildItem -Path $decal, $directx -Recurse -File -Filter $name | Where-Object { $_.Name -eq $name })
    if ($referenceCopies.Count -eq 0) { throw "Missing build reference: $name" }
    # Decal includes identical PIA copies in its root and .NET 4.0 PIA folder.
    $hashes = @($referenceCopies | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Select-Object -Unique)
    if ($hashes.Count -ne 1) { throw "Conflicting copies of build reference: $name" }
    Copy-Item $referenceCopies[0].FullName (Join-Path $references $name)
}
Write-Output "Build references extracted to $references"
