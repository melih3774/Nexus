<#
.SYNOPSIS
    Builds the Nexus HID driver, stages the package, and validates it. Installs nothing.

.DESCRIPTION
    The WDK's MSBuild integration ships as a Visual Studio extension that a Build Tools install does
    not have, so the driver project builds with the plain v143 toolset on any edition and the
    packaging pipeline is not run for it. This script is that pipeline, invoked directly:

        build -> stage -> StampInf -> Inf2Cat

    It deliberately stops before signing. Creating a certificate and trusting it in the machine's
    stores is the one step that changes what this computer trusts, and it belongs to a deliberate
    owner-driven install pass, not to a validation script. See tools/nexus-hid-install.ps1 and
    docs/getting-started.md.

    Nothing here touches the Driver Store, the certificate stores, Secure Boot, HVCI or TESTSIGNING.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER StageRoot
    Where to stage the package. Defaults to the canonical location, artifacts\NexusHidPackage.

    THE CANONICAL PACKAGE

    A catalog is only meaningful next to the exact INF and DLL it hashes. Staging to a temp
    directory meant the installer looked elsewhere and reported "catalog not generated" while a
    perfectly good one existed - and the tempting fix, copying the .cat next to the unstamped INF,
    would have produced a catalog that does not match its own package. So there is one canonical
    output directory and both scripts use it.

    artifacts\ is git-ignored: a stamped INF differs from the committed one by a date, and a signed
    catalog is machine-specific. Neither belongs in the repository.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $StageRoot
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$driverDir = Join-Path $repoRoot 'src\driver\NexusHid'
$project = Join-Path $driverDir 'NexusHidDriver.vcxproj'

# The package version. Kept beside the product version rather than invented here.
#
# 0.9.2.2 is the power-management fix: the INF stops loading vhf a second time and the driver gives
# up power policy ownership to it. 0.9.2.3 is the same driver in a declarative package again (no
# DelReg), so the Driver Store reference-counts its UMDF registration, and with the C runtime linked in
# rather than imported from the Visual C++ redistributable. 0.9.2.4 changes nothing but the number: it
# exists to prove on a real PC that a driver update loads at once. It must match NEXUS_HID_DRIVER_VERSION in
# Driver.c.
$driverVersion = '0.9.2.4'

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

    # The project pins its toolset (v143, Visual Studio 2022) and the Windows Kit (10.0.26100.0), so a
    # newer Visual Studio alone does not build it. Say what is needed rather than only what is missing.
    throw 'MSBuild from Visual Studio 2022 not found. The driver needs Visual Studio 2022 (Build Tools or any edition) ' +
          'with the "Desktop development with C++" workload (MSVC v143), plus the Windows SDK and WDK 10.0.26100.'
}

function Find-Dumpbin {
    # Beside the compiler that just built the driver: <VS>\VC\Tools\MSVC\<version>\bin\Hostx64\x64.
    $vs = Split-Path (Split-Path (Split-Path (Split-Path (Split-Path (Find-MSBuild)))))
    $hit = Get-ChildItem -Path (Join-Path $vs 'VC\Tools\MSVC') -Recurse -Filter 'dumpbin.exe' -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -like '*\Hostx64\x64\*' } |
           Sort-Object FullName -Descending |
           Select-Object -First 1
    if ($null -eq $hit) { throw "dumpbin.exe not found under $vs\VC\Tools\MSVC." }
    return $hit.FullName
}

Write-Output '== Building the driver =='
$msbuild = Find-MSBuild
& $msbuild $project "-p:Configuration=$Configuration" -p:Platform=x64 -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "driver build failed ($LASTEXITCODE)" }

$dll = Join-Path $driverDir "x64\$Configuration\NexusHidDriver.dll"
if (-not (Test-Path $dll)) { throw "expected output missing: $dll" }

Write-Output '== Imports =='
# WUDFHost loads the driver on machines that have nothing but Windows. Every DLL it imports must be one
# Windows ships: VhfUm and the core system DLLs, and the Universal CRT's api-ms-win-crt-* sets (inbox
# since Windows 10). VCRUNTIME140.dll is the one this refuses in practice - a /MD build imports it, and
# without the Visual C++ redistributable the driver never loads and the device silently runs as vhf.
$dumpbin = Find-Dumpbin
$imports = & $dumpbin /nologo /dependents $dll |
           Where-Object { $_ -match '^\s+(\S+\.dll)\s*$' } |
           ForEach-Object { $Matches[1] }
if ($LASTEXITCODE -ne 0 -or $imports.Count -eq 0) { throw "dumpbin could not list the imports of $dll" }

$system = @('VhfUm.dll', 'ntdll.dll', 'KERNEL32.dll')
$foreign = @($imports | Where-Object { $system -notcontains $_ -and $_ -notlike 'api-ms-win-crt-*' })
$imports | ForEach-Object { Write-Output "   $_" }
if ($foreign.Count -gt 0) {
    throw "NexusHidDriver.dll imports DLLs Windows does not ship: $($foreign -join ', '). Link the C runtime statically (RuntimeLibrary MultiThreaded in NexusHidDriver.vcxproj)."
}

Write-Output '== Staging =='
# The canonical package directory. Git-ignored, so a stamped INF and a signed catalog never reach a
# commit, but stable enough that the installer finds it without being told.
if (-not $StageRoot) {
    $StageRoot = Join-Path $repoRoot 'artifacts\NexusHidPackage'
}
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
Write-Output "Canonical package: $StageRoot"
Write-Output 'Package is valid and UNSIGNED. Signing and installation are a separate, deliberate'
Write-Output 'owner-driven pass - run tools\nexus-hid-install.ps1 -Action Install for the plan.'
Write-Output 'Nothing was installed.'

