<#
.SYNOPSIS
    Builds the Nexus HID driver, stages the package, and validates it. Installs nothing.

.DESCRIPTION
    The WDK's MSBuild integration ships as a Visual Studio extension and this machine has Build
    Tools rather than full Visual Studio, so the driver project builds with the v143 toolset and the
    packaging pipeline is not run for it. This script is that pipeline, invoked directly:

        build -> stage -> StampInf -> Inf2Cat

    It deliberately stops before signing. Creating a certificate and trusting it in the machine's
    stores is the one step that changes what this computer trusts, and it belongs to a deliberate
    owner-driven install pass, not to a validation script. See docs/virtual-hid.md section 11.

    Nothing here touches the Driver Store, the certificate stores, Secure Boot, HVCI or TESTSIGNING.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER StageRoot
    Where to stage the package. Defaults to a temp directory, never the repository.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $StageRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'NexusHidPackage')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$driverDir = Join-Path $repoRoot 'src\driver\NexusHid'
$project = Join-Path $driverDir 'NexusHidDriver.vcxproj'

# The package version. Kept beside the product version rather than invented here.
$driverVersion = '0.9.0.0'

function Find-Tool {
    param([string] $Name, [string[]] $PreferredArch = @('x64', 'x86'))

    $kit = 'C:\Program Files (x86)\Windows Kits\10\bin'
    foreach ($arch in $PreferredArch) {
        $hit = Get-ChildItem -Path $kit -Recurse -Filter $Name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like "*\$arch\*" } |
               Select-Object -First 1
        if ($null -ne $hit) { return $hit.FullName }
    }

    throw "$Name not found under $kit. Is the WDK installed?"
}

function Find-MSBuild {
    $candidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe'
    )

    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw 'MSBuild not found.'
}

Write-Output '== Building the driver =='
$msbuild = Find-MSBuild
& $msbuild $project "-p:Configuration=$Configuration" -p:Platform=x64 -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "driver build failed ($LASTEXITCODE)" }

$dll = Join-Path $driverDir "x64\$Configuration\NexusHidDriver.dll"
if (-not (Test-Path $dll)) { throw "expected output missing: $dll" }

Write-Output '== Staging =='
# Staged outside the repository: a stamped INF differs from the committed one by a date, and that
# difference must never end up in a commit.
if (Test-Path $StageRoot) { Remove-Item -Recurse -Force $StageRoot }
New-Item -ItemType Directory -Force $StageRoot | Out-Null

Copy-Item (Join-Path $driverDir 'NexusHid.inf') $StageRoot
Copy-Item $dll $StageRoot

Write-Output "   staged to $StageRoot"

Write-Output '== StampInf =='
# Substitutes $ARCH$ in the decorated sections and fills in DriverVer. Without it Inf2Cat rejects
# the package for having no NTAMD64 sections and no DriverVer - both were seen, and both were this.
$stampinf = Find-Tool 'stampinf.exe'
& $stampinf -f (Join-Path $StageRoot 'NexusHid.inf') -d '*' -v $driverVersion -a 'amd64'
if ($LASTEXITCODE -ne 0) { throw "stampinf failed ($LASTEXITCODE)" }

Write-Output '== Inf2Cat =='
# Generates the catalog and runs the signability test. Errors here are package errors.
$inf2cat = Find-Tool 'Inf2Cat.exe' @('x86', 'x64')
& $inf2cat "/driver:$StageRoot" /os:10_x64
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat signability test failed ($LASTEXITCODE)" }

$cat = Get-ChildItem -Path $StageRoot -Filter '*.cat' | Select-Object -First 1
if ($null -eq $cat) { throw 'no catalog produced' }

Write-Output ''
Write-Output '== Result =='
Get-ChildItem $StageRoot | ForEach-Object { Write-Output ("   {0,-24} {1,8} bytes" -f $_.Name, $_.Length) }
Write-Output ''
Write-Output 'Package is valid and UNSIGNED. Signing and installation are a separate, deliberate'
Write-Output 'owner-driven pass - see docs/virtual-hid.md section 11. Nothing was installed.'
