<#
.SYNOPSIS
    Inspects, plans and (on explicit request) performs the Nexus HID driver install. Dry-run by
    default; nothing is changed unless -Apply is passed AND the owner confirms.

.DESCRIPTION
    This is the whole of install day in one script, written so that the deliberate pass is a matter
    of reading output and answering one prompt rather than remembering nine commands.

    WHAT IT WILL NEVER DO
      - disable or weaken Secure Boot, HVCI / Memory Integrity, TESTSIGNING or UAC
      - install anything if the package does not verify
      - import a certificate without naming it, showing its thumbprint, and being told to

    It attempts the *secure* configuration first and only that one. If Windows refuses the package
    with Secure Boot on, HVCI on and test-signing off, the correct response is to stop and report -
    which is what this does. Turning off a protection to make a driver load would defeat the reason
    the driver exists.

.PARAMETER Action
    Inspect (default), Install, Repair, Uninstall.

.PARAMETER Apply
    Actually perform the action. Without it every action is a dry run that reports what it would do.

.EXAMPLE
    .\nexus-hid-install.ps1
    .\nexus-hid-install.ps1 -Action Install            # dry run: prints the exact plan
    .\nexus-hid-install.ps1 -Action Install -Apply     # the real thing, with confirmation
#>
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Install', 'Repair', 'Uninstall')]
    [string] $Action = 'Inspect',

    [switch] $Apply,

    [string] $PackageDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$driverDir = Join-Path $repoRoot 'src\driver\NexusHid'

# Identity, mirrored from NexusHidProtocol.h / NexusHidProtocol.cs.
$HardwareId = 'Root\NexusHid'
$InfName = 'NexusHid.inf'
$DriverDll = 'NexusHidDriver.dll'
$CatalogName = 'nexushid.cat'
$CertSubject = 'CN=Nexus Driver Signing'
$ExpectedVersion = '0.9.0.0'

function Section([string] $title) {
    Write-Output ''
    Write-Output "== $title =="
}

function Field([string] $name, $value) {
    Write-Output ("   {0,-28} {1}" -f $name, $value)
}

# --------------------------------------------------------------- machine security posture

function Get-SecurityPosture {
    # Confirm-SecureBootUEFI needs elevation and returns nothing useful without it, so fall back to
    # the registry, which is readable as the owner. Reporting "unknown" when the answer is available
    # would make the posture check useless in exactly the unelevated case it is normally run in.
    $secureBoot = try { Confirm-SecureBootUEFI } catch { $null }

    if ($null -eq $secureBoot) {
        try {
            $v = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' `
                -Name UEFISecureBootEnabled -ErrorAction Stop
            $secureBoot = ($v.UEFISecureBootEnabled -eq 1)
        } catch { $secureBoot = $null }
    }

    $dg = try {
        Get-CimInstance -ClassName Win32_DeviceGuard `
            -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction Stop
    } catch { $null }

    $hvci = $false
    if ($null -ne $dg -and $null -ne $dg.SecurityServicesRunning) {
        $hvci = $dg.SecurityServicesRunning -contains 2
    }

    $testSigning = $false
    try {
        $bcd = & bcdedit 2>$null | Out-String
        $testSigning = $bcd -match 'testsigning\s+Yes'
    } catch { }

    [pscustomobject]@{
        SecureBoot  = $secureBoot
        Hvci        = $hvci
        TestSigning = $testSigning
    }
}

# --------------------------------------------------------------- package inspection

function Get-PackageState {
    $dir = if ($PackageDir) { $PackageDir } else { Join-Path $driverDir 'x64\Release' }

    $inf = Join-Path $driverDir $InfName
    $dll = Join-Path $dir $DriverDll
    $cat = Join-Path $dir $CatalogName

    $dllHash = if (Test-Path $dll) { (Get-FileHash $dll -Algorithm SHA256).Hash } else { $null }
    $infHash = if (Test-Path $inf) { (Get-FileHash $inf -Algorithm SHA256).Hash } else { $null }

    $signature = if (Test-Path $cat) { Get-AuthenticodeSignature $cat } else { $null }

    [pscustomobject]@{
        Directory   = $dir
        Inf         = $inf
        InfPresent  = Test-Path $inf
        InfHash     = $infHash
        Dll         = $dll
        DllPresent  = Test-Path $dll
        DllHash     = $dllHash
        DllVersion  = if (Test-Path $dll) { (Get-Item $dll).VersionInfo.FileVersion } else { $null }
        Catalog     = $cat
        CatPresent  = Test-Path $cat
        CatStatus   = if ($null -ne $signature) { $signature.Status.ToString() } else { 'NotSigned' }
        CatSigner   = if ($null -ne $signature -and $null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    }
}

function Get-InstalledState {
    $inStore = @()
    try {
        $inStore = & pnputil /enum-drivers 2>$null | Select-String -Pattern 'NexusHid' -Context 4, 0
    } catch { }

    $devices = @()
    try {
        $devices = Get-PnpDevice -ErrorAction SilentlyContinue |
                   Where-Object { $_.InstanceId -like '*NexusHid*' }
    } catch { }

    $certs = @()
    foreach ($store in 'Root', 'TrustedPublisher') {
        try {
            $certs += Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue |
                      Where-Object { $_.Subject -eq $CertSubject } |
                      ForEach-Object { [pscustomobject]@{ Store = $store; Thumbprint = $_.Thumbprint } }
        } catch { }
    }

    [pscustomobject]@{
        InDriverStore = ($inStore.Count -gt 0)
        Devices       = $devices
        DeviceCount   = @($devices).Count
        TrustedCerts  = $certs
    }
}

# --------------------------------------------------------------- report

$posture = Get-SecurityPosture
$package = Get-PackageState
$installed = Get-InstalledState

Section 'Machine security posture (must not change)'
Field 'Secure Boot' $(if ($null -eq $posture.SecureBoot) { 'unknown' } elseif ($posture.SecureBoot) { 'ON' } else { 'off (pre-existing; Nexus neither needs nor changes this)' })
Field 'HVCI / Memory Integrity' $(if ($posture.Hvci) { 'ON' } else { 'off' })
Field 'TESTSIGNING' $(if ($posture.TestSigning) { 'ON  <-- must be off' } else { 'off' })

Section 'Package'
Field 'Directory' $package.Directory
Field 'INF' $(if ($package.InfPresent) { "present  $($package.InfHash.Substring(0,16))..." } else { 'MISSING' })
Field 'Driver DLL' $(if ($package.DllPresent) { "present  $($package.DllHash.Substring(0,16))..." } else { 'MISSING (build it first)' })
Field 'DLL version' $(if ($package.DllVersion) { $package.DllVersion } else { '-' })
Field 'Catalog' $(if ($package.CatPresent) { $package.CatStatus } else { 'not generated' })
Field 'Catalog signer' $(if ($package.CatSigner) { $package.CatSigner } else { '- (unsigned)' })

Section 'Current install state'
Field 'In Driver Store' $(if ($installed.InDriverStore) { 'YES' } else { 'no' })
Field 'Nexus devices' $installed.DeviceCount
Field 'Trusted Nexus certs' $(if ($installed.TrustedCerts.Count -eq 0) { 'none' } else { ($installed.TrustedCerts | ForEach-Object { "$($_.Store):$($_.Thumbprint)" }) -join ', ' })

# --------------------------------------------------------------- safety assertions

$blockers = @()

if ($posture.TestSigning) {
    $blockers += 'TESTSIGNING is ON. Nexus does not install under test signing; the whole point is a package that loads on a normally secured machine.'
}
if (-not $package.InfPresent) { $blockers += "INF missing: $($package.Inf)" }
if (-not $package.DllPresent) { $blockers += "Driver DLL missing: run tools\validate-driver-package.ps1 first" }

if ($Action -in @('Install', 'Repair')) {
    if (-not $package.CatPresent) {
        $blockers += 'No catalog. Generate it with validate-driver-package.ps1, then sign it (see the signing plan below).'
    }
    elseif ($package.CatStatus -ne 'Valid') {
        $blockers += "Catalog signature is '$($package.CatStatus)'. Windows will refuse an unsigned or untrusted package, and that refusal is correct."
    }
}

if ($blockers.Count -gt 0) {
    Section 'Blockers'
    $blockers | ForEach-Object { Write-Output "   - $_" }
}

# --------------------------------------------------------------- plans

Section "Plan: $Action"

switch ($Action) {
    'Inspect' {
        Write-Output '   Nothing to do. This is a read-only report.'
    }

    'Install' {
        Write-Output '   1. Create a Nexus-owned code-signing certificate (once, kept local):'
        Write-Output "        New-SelfSignedCertificate -Type CodeSigningCert -Subject '$CertSubject' ``"
        Write-Output "            -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature ``"
        Write-Output "            -TextExtension '2.5.29.37={text}1.3.6.1.5.5.7.3.3' -NotAfter (Get-Date).AddYears(5)"
        Write-Output '      EKU 1.3.6.1.5.5.7.3.3 is Code Signing, and it is the only one this needs.'
        Write-Output ''
        Write-Output '   2. Sign the catalog with it:'
        Write-Output "        signtool sign /fd SHA256 /a /n 'Nexus Driver Signing' '$($package.Catalog)'"
        Write-Output '      No timestamp is used deliberately: a timestamp exists so signatures outlive the'
        Write-Output '      certificate, which matters for distribution and not for one machine that can'
        Write-Output '      re-sign at will. Re-signing is preferable to a signature that survives revocation.'
        Write-Output ''
        Write-Output '   3. Trust exactly that certificate, in exactly two Local Machine stores (admin):'
        Write-Output '        Root             - so the chain validates'
        Write-Output '        TrustedPublisher - so PnP installs without prompting'
        Write-Output '      Export the .cer from CurrentUser\My and import only that file. This is the one'
        Write-Output '      step that changes what this computer trusts; it must name the certificate and'
        Write-Output '      show its thumbprint before doing it.'
        Write-Output ''
        Write-Output '   4. Verify before installing:'
        Write-Output "        Get-AuthenticodeSignature '$($package.Catalog)'   # expect Status = Valid"
        Write-Output ''
        Write-Output '   5. Add the package (admin):'
        Write-Output "        pnputil /add-driver '$($package.Inf)' /install"
        Write-Output ''
        Write-Output '   6. Confirm the security posture did NOT move:'
        Write-Output '        Secure Boot still ON, HVCI still ON, TESTSIGNING still off.'
        Write-Output '      If any changed, the install is wrong. Roll it back.'
        Write-Output ''
        Write-Output '   7. Verify enumeration before trusting anything:'
        Write-Output "        Get-PnpDevice | Where-Object InstanceId -like '*NexusHid*'"
        Write-Output '      Expect Nexus Mouse, Nexus Keyboard, Nexus Touch. input.status reporting'
        Write-Output '      VIRTUAL_HID without enumerated devices is a failed install, not a working one.'
    }

    'Repair' {
        Write-Output "   1. Compare installed package version against $ExpectedVersion."
        Write-Output '   2. If it differs: pnputil /delete-driver <oem#.inf> /uninstall, then re-add.'
        Write-Output '   3. Check for duplicate root devices before adding - more than one Root\NexusHid'
        Write-Output '      means a previous install did not clean up, and a second device would publish'
        Write-Output '      a second set of HID collections.'
        Write-Output '   4. Re-verify enumeration and identity resolution afterwards.'
    }

    'Uninstall' {
        Write-Output '   1. Neutralize: PC Core releases held HID state through the HID backend first.'
        Write-Output '   2. Stop routing to Virtual HID and let the router fall back.'
        Write-Output '   3. Remove the device:'
        Write-Output "        pnputil /remove-device '$HardwareId'"
        Write-Output '   4. Remove the package:'
        Write-Output '        pnputil /delete-driver <oem#.inf> /uninstall'
        Write-Output '   5. Remove the certificate from BOTH stores - uninstall that leaves the key'
        Write-Output '      trusted leaves the machine trusting something for no reason:'
        Write-Output "        Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |"
        Write-Output "            Where-Object Subject -eq '$CertSubject' | Remove-Item"
        Write-Output '   6. Clear trusted Nexus identities in PC Core (happens automatically on backend loss).'
    }
}

# --------------------------------------------------------------- apply

Section 'Result'

if (-not $Apply) {
    Write-Output '   DRY RUN. Nothing was changed.'
    Write-Output '   Re-run with -Apply to perform the action, which will ask for confirmation first.'
    return
}

if ($blockers.Count -gt 0) {
    Write-Output '   REFUSED. Resolve the blockers above first.'
    Write-Output '   This script will not weaken Secure Boot, HVCI, TESTSIGNING or UAC to make an install succeed.'
    exit 1
}

if ($Action -eq 'Inspect') {
    Write-Output '   Inspect changes nothing; -Apply has no effect.'
    return
}

Write-Output "   About to $Action the Nexus HID driver on this machine."
Write-Output '   This changes machine state. Read the plan above before answering.'
$answer = Read-Host "   Type the word $($Action.ToUpperInvariant()) to proceed"

if ($answer -cne $Action.ToUpperInvariant()) {
    Write-Output '   Cancelled. Nothing was changed.'
    return
}

throw 'Apply is not implemented yet, deliberately. The first real install is an owner-driven pass ' +
      'that should be watched command by command, not delegated to a script that has never run.'
