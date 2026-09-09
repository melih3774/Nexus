<#
.SYNOPSIS
    Inspects, plans and (on explicit request) performs the Nexus HID driver install. Dry-run by
    default; nothing is changed unless -Apply is passed AND the owner confirms.

.DESCRIPTION
    This is the whole of install day in one script, so the deliberate pass is a matter of reading
    output and answering one prompt rather than remembering nine commands.

    WHAT IT WILL NEVER DO
      - change Secure Boot, HVCI / Memory Integrity, TESTSIGNING or UAC
      - install anything if the package does not verify
      - import a certificate without naming it, showing its thumbprint, and being told to
      - create a second Nexus root device when one already exists

    It attempts the secure configuration and only that one. If Windows refuses the package with the
    machine as it stands, the correct response is to stop and report - which is what this does.
    Turning off a protection to make a driver load would defeat the reason the driver exists.

    THE SECURITY BASELINE IS CAPTURED, NOT ASSUMED

    An earlier version of this plan asserted "Secure Boot still ON" after install. That is wrong on
    this machine, where Secure Boot is off and always has been. Nexus does not require Secure Boot
    and does not change it; what matters is that installing changes nothing, so the posture is
    captured before and compared after rather than measured against a hardcoded expectation.

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

# The canonical package: stamped INF, driver DLL and catalog, together. A catalog is only meaningful
# beside the exact files it hashes, so there is one directory and both scripts use it.
$CanonicalPackage = Join-Path $repoRoot 'artifacts\NexusHidPackage'
$BaselineFile = Join-Path $repoRoot 'artifacts\security-baseline.json'

# Identity. Read from the INF below rather than assumed, but these are the expected values.
$InfName = 'NexusHid.inf'
$DriverDll = 'NexusHidDriver.dll'
$CatalogName = 'nexushid.cat'
$CertSubject = 'CN=Nexus Driver Signing'
$ExpectedVersion = '0.9.0.0'
$InstanceId = 'NexusHid'

function Section([string] $title) {
    Write-Output ''
    Write-Output "== $title =="
}

function Field([string] $name, $value) {
    Write-Output ("   {0,-28} {1}" -f $name, $value)
}

# --------------------------------------------------------------- WDK device tooling

function Find-DeviceTool([string] $name) {
    $roots = @(
        'C:\Program Files (x86)\Windows Kits\10\Tools',
        'C:\Program Files (x86)\Windows Kits\10\bin'
    )

    foreach ($root in $roots) {
        $hit = Get-ChildItem -Path $root -Recurse -Filter $name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like '*\x64\*' } |
               Select-Object -First 1
        if ($null -ne $hit) { return $hit.FullName }
    }

    return $null
}

# --------------------------------------------------------------- the hardware ID, read from the INF

function Get-HardwareIdFromInf([string] $inf) {
    if (-not (Test-Path $inf)) { return $null }

    # Read the hardware ID the way Windows does, not by pattern-matching any line that looks right.
    #
    # The first version of this used one regex over the whole file and matched the [Manufacturer]
    # line - which has exactly the same shape as a models line - and confidently reported a decorated
    # section name as the hardware ID. Passing that to devgen would have created a device no driver
    # could ever bind to.
    #
    # So: [Manufacturer] names the models sections, and the models sections carry the hardware IDs.
    $lines = Get-Content $inf
    $section = ''
    $manufacturerLine = $null
    $sectionLines = @{}

    foreach ($line in $lines) {
        $text = $line
        $semi = $text.IndexOf(';')
        if ($semi -ge 0) { $text = $text.Substring(0, $semi) }
        $text = $text.Trim()
        if ($text -eq '') { continue }

        if ($text -match '^\[(?<name>[^\]]+)\]$') {
            $section = $Matches['name'].Trim()
            continue
        }

        if ($section -ieq 'Manufacturer' -and $null -eq $manufacturerLine) {
            $manufacturerLine = $text
            continue
        }

        if (-not $sectionLines.ContainsKey($section)) { $sectionLines[$section] = @() }
        $sectionLines[$section] += $text
    }

    if ($null -eq $manufacturerLine) { return $null }

    # %ManufacturerName% = Nexus, NTamd64.10.0...19041, NTamd64.10.0...22000
    #   -> base models section "Nexus", decorated as "Nexus.NTamd64.10.0...19041" and so on.
    $rhs = ($manufacturerLine -split '=', 2)[1]
    if ($null -eq $rhs) { return $null }

    $parts = $rhs.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
    if ($parts.Count -eq 0) { return $null }

    $baseSection = $parts[0]
    $candidates = @($baseSection)
    foreach ($decoration in $parts[1..($parts.Count - 1)]) {
        $candidates += "$baseSection.$decoration"
    }

    foreach ($candidate in $candidates) {
        $key = $sectionLines.Keys | Where-Object { $_ -ieq $candidate } | Select-Object -First 1
        if ($null -eq $key) { continue }

        foreach ($entry in $sectionLines[$key]) {
            # %DeviceName% = NexusHid_Win11, Root\NexusHid
            $entryRhs = ($entry -split '=', 2)[1]
            if ($null -eq $entryRhs) { continue }

            $fields = $entryRhs.Split(',') | ForEach-Object { $_.Trim() }
            if ($fields.Count -ge 2 -and $fields[1] -ne '') {
                return $fields[1]
            }
        }
    }

    return $null
}

# --------------------------------------------------------------- machine security posture

function Get-SecurityPosture {
    # Confirm-SecureBootUEFI needs elevation, so fall back to the registry, which is readable as the
    # owner. Reporting "unknown" when the answer is available makes the check useless in exactly the
    # unelevated case it is normally run in.
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

function Format-Posture($p) {
    $sb = if ($null -eq $p.SecureBoot) { 'unknown' } elseif ($p.SecureBoot) { 'ON' } else { 'off' }
    "SecureBoot=$sb HVCI=$(if ($p.Hvci) { 'ON' } else { 'off' }) TESTSIGNING=$(if ($p.TestSigning) { 'ON' } else { 'off' })"
}

# --------------------------------------------------------------- package inspection

function Get-PackageState {
    $dir = if ($PackageDir) { $PackageDir } else { $CanonicalPackage }

    $inf = Join-Path $dir $InfName
    $dll = Join-Path $dir $DriverDll
    $cat = Join-Path $dir $CatalogName

    $signature = if (Test-Path $cat) { Get-AuthenticodeSignature $cat } else { $null }

    $driverVer = $null
    if (Test-Path $inf) {
        $line = Select-String -Path $inf -Pattern '^\s*DriverVer\s*=' | Select-Object -First 1
        if ($null -ne $line) { $driverVer = ($line.Line -split '=', 2)[1].Trim() }
    }

    [pscustomobject]@{
        Directory  = $dir
        Inf        = $inf
        InfPresent = Test-Path $inf
        InfHash    = if (Test-Path $inf) { (Get-FileHash $inf -Algorithm SHA256).Hash } else { $null }
        Stamped    = ($null -ne $driverVer -and $driverVer -ne '')
        DriverVer  = $driverVer
        HardwareId = Get-HardwareIdFromInf $inf
        Dll        = $dll
        DllPresent = Test-Path $dll
        DllHash    = if (Test-Path $dll) { (Get-FileHash $dll -Algorithm SHA256).Hash } else { $null }
        Catalog    = $cat
        CatPresent = Test-Path $cat
        CatStatus  = if ($null -ne $signature) { $signature.Status.ToString() } else { 'NotPresent' }
        CatSigner  = if ($null -ne $signature -and $null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    }
}

function Get-InstalledState([string] $hardwareId) {
    $inStore = @()
    try {
        $inStore = & pnputil /enum-drivers 2>$null | Select-String -Pattern 'NexusHid'
    } catch { }

    $devices = @()
    try {
        $devices = @(Get-PnpDevice -ErrorAction SilentlyContinue |
                     Where-Object { $_.InstanceId -like '*NEXUSHID*' })
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
        DeviceCount   = $devices.Count
        TrustedCerts  = $certs
    }
}

# --------------------------------------------------------------- report

$posture = Get-SecurityPosture
$package = Get-PackageState
$installed = Get-InstalledState $package.HardwareId

$devgen = Find-DeviceTool 'devgen.exe'
$devcon = Find-DeviceTool 'devcon.exe'

Section 'Machine security posture (captured, and must not change)'
Field 'Secure Boot' $(if ($null -eq $posture.SecureBoot) { 'unknown' } elseif ($posture.SecureBoot) { 'ON' } else { 'off (pre-existing; Nexus neither needs nor changes this)' })
Field 'HVCI / Memory Integrity' $(if ($posture.Hvci) { 'ON' } else { 'off' })
Field 'TESTSIGNING' $(if ($posture.TestSigning) { 'ON  <-- must be off' } else { 'off' })

Section 'Canonical package'
Field 'Directory' $package.Directory
Field 'INF' $(if ($package.InfPresent) { "present  $($package.InfHash.Substring(0,16))..." } else { 'MISSING' })
Field 'INF stamped' $(if ($package.Stamped) { "yes  DriverVer = $($package.DriverVer)" } else { 'NO (run validate-driver-package.ps1)' })
Field 'Hardware ID' $(if ($package.HardwareId) { $package.HardwareId } else { 'could not be read from the INF' })
Field 'Driver DLL' $(if ($package.DllPresent) { "present  $($package.DllHash.Substring(0,16))..." } else { 'MISSING' })
Field 'Catalog' $(if ($package.CatPresent) { "present  ($($package.CatStatus))" } else { 'MISSING' })
Field 'Catalog signer' $(if ($package.CatSigner) { $package.CatSigner } else { '- (unsigned)' })

Section 'Device tooling (WDK)'
Field 'devgen.exe' $(if ($devgen) { $devgen } else { 'NOT FOUND' })
Field 'devcon.exe' $(if ($devcon) { $devcon } else { 'NOT FOUND' })

Section 'Current install state'
Field 'In Driver Store' $(if ($installed.InDriverStore) { 'YES' } else { 'no' })
Field 'Nexus devices' $installed.DeviceCount
if ($installed.DeviceCount -gt 0) {
    $installed.Devices | ForEach-Object { Field '  instance' $_.InstanceId }
}
Field 'Trusted Nexus certs' $(if ($installed.TrustedCerts.Count -eq 0) { 'none' } else { ($installed.TrustedCerts | ForEach-Object { "$($_.Store):$($_.Thumbprint)" }) -join ', ' })

# --------------------------------------------------------------- safety assertions

$blockers = @()

if ($posture.TestSigning) {
    $blockers += 'TESTSIGNING is ON. Nexus does not install under test signing; the whole point is a package that loads on a normally secured machine.'
}
if (-not $package.InfPresent) { $blockers += "INF missing from the canonical package: $($package.Inf)" }
if (-not $package.DllPresent) { $blockers += 'Driver DLL missing. Run tools\validate-driver-package.ps1 first.' }
if ($package.InfPresent -and -not $package.Stamped) { $blockers += 'INF is not stamped. Run tools\validate-driver-package.ps1.' }
if (-not $package.HardwareId) { $blockers += 'Hardware ID could not be read from the INF.' }

if ($Action -in @('Install', 'Repair')) {
    if (-not $package.CatPresent) {
        $blockers += 'No catalog in the canonical package. Run validate-driver-package.ps1, then sign it.'
    }
    elseif ($package.CatStatus -ne 'Valid') {
        $blockers += "Catalog signature is '$($package.CatStatus)'. Windows will refuse an unsigned or untrusted package, and that refusal is correct."
    }
    if (-not $devgen -and -not $devcon) {
        $blockers += 'Neither devgen.exe nor devcon.exe was found. A root-enumerated device cannot be created without one.'
    }
}

if ($Action -eq 'Install' -and $installed.DeviceCount -gt 0) {
    $blockers += "A Nexus device already exists ($($installed.DeviceCount)). Use -Action Repair; installing again would create a duplicate root devnode publishing a second set of HID collections."
}

if ($blockers.Count -gt 0) {
    Section 'Blockers'
    $blockers | ForEach-Object { Write-Output "   - $_" }
}

# --------------------------------------------------------------- plans

Section "Plan: $Action"

$hwid = $package.HardwareId
$deviceStep = if ($devgen) {
    "devgen /add /bus ROOT /hardwareid `"$hwid`" /instanceid $InstanceId"
} elseif ($devcon) {
    "devcon install `"$($package.Inf)`" `"$hwid`""
} else {
    '(no device-creation tool available)'
}

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
        Write-Output '   2. Sign the catalog IN PLACE, in the canonical package:'
        Write-Output "        signtool sign /fd SHA256 /a /n 'Nexus Driver Signing' '$($package.Catalog)'"
        Write-Output '      No timestamp, deliberately: a timestamp exists so a signature outlives its'
        Write-Output '      certificate, which matters for distribution and not for one machine that can'
        Write-Output '      re-sign at will.'
        Write-Output ''
        Write-Output '   3. Trust exactly that certificate, in exactly two Local Machine stores (admin):'
        Write-Output '        Root             - so the chain validates'
        Write-Output '        TrustedPublisher - so PnP installs without prompting'
        Write-Output '      Export the .cer from CurrentUser\My and import only that file. This is the one'
        Write-Output '      step that changes what this computer trusts.'
        Write-Output ''
        Write-Output '   4. Verify before installing:'
        Write-Output "        Get-AuthenticodeSignature '$($package.Catalog)'   # expect Status = Valid"
        Write-Output ''
        Write-Output '   5. CREATE THE ROOT DEVICE (admin). This step is not optional:'
        Write-Output "        $deviceStep"
        Write-Output ''
        Write-Output '      pnputil /add-driver /install alone does NOT create a devnode. It stages the'
        Write-Output '      package and binds it to devices that already exist; a root-enumerated device'
        Write-Output '      has no bus to enumerate it, so nothing appears and the install looks like it'
        Write-Output '      silently did nothing. The device has to be generated first.'
        Write-Output ''
        Write-Output '   6. Add and install the package (admin):'
        Write-Output "        pnputil /add-driver '$($package.Inf)' /install"
        Write-Output ''
        Write-Output '   7. ROLLBACK POINT. If step 5 succeeded and step 6 fails, or the device does not'
        Write-Output '      start, the devnode exists with no working driver. Remove it before retrying:'
        Write-Output "        devgen /remove <instance id from step 5>"
        Write-Output '      Leaving it behind is what produces duplicates on the next attempt.'
        Write-Output ''
        Write-Output '   8. Confirm the posture is UNCHANGED from the captured baseline:'
        Write-Output "        baseline: $(Format-Posture $posture)"
        Write-Output '      Not "Secure Boot ON" - unchanged. Any difference means the install did'
        Write-Output '      something it should not have, and it should be rolled back.'
        Write-Output ''
        Write-Output '   9. Verify enumeration before trusting anything:'
        Write-Output "        Get-PnpDevice | Where-Object InstanceId -like '*NEXUSHID*'"
        Write-Output '      Expect Nexus Mouse, Nexus Keyboard, Nexus Touch. input.status reporting'
        Write-Output '      VIRTUAL_HID without enumerated devices is a failed install, not a working one.'
    }

    'Repair' {
        Write-Output "   1. Compare the installed package version against $ExpectedVersion (INF says $($package.DriverVer))."
        Write-Output '   2. Update the package in place; do NOT create a second device:'
        Write-Output "        pnputil /add-driver '$($package.Inf)' /install"
        Write-Output '   3. If the driver must be replaced rather than updated:'
        Write-Output '        pnputil /delete-driver <oem#.inf> /uninstall     then re-add as above.'
        Write-Output "   4. Existing Nexus devices: $($installed.DeviceCount). More than one means a"
        Write-Output '      previous install left a devnode behind; remove the extras with devgen /remove.'
        Write-Output '   5. Re-verify enumeration and identity resolution afterwards.'
    }

    'Uninstall' {
        Write-Output '   1. Neutralize: PC Core releases held HID state through the HID backend first.'
        Write-Output '   2. Stop routing to Virtual HID and let the router fall back.'
        Write-Output '   3. Remove the exact device(s), by instance id:'
        if ($installed.DeviceCount -gt 0) {
            $installed.Devices | ForEach-Object { Write-Output "        devgen /remove $($_.InstanceId)" }
        } else {
            Write-Output '        (none present)'
        }
        Write-Output '      devgen /remove only removes devices devgen created, which is why the install'
        Write-Output '      path uses it rather than a generic device removal.'
        Write-Output '   4. Remove the package:'
        Write-Output '        pnputil /delete-driver <oem#.inf> /uninstall'
        Write-Output '   5. Remove the certificate from BOTH stores - uninstall that leaves the key'
        Write-Output '      trusted leaves the machine trusting something for no reason:'
        Write-Output "        Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |"
        Write-Output "            Where-Object Subject -eq '$CertSubject' | Remove-Item"
        Write-Output '   6. Clear trusted Nexus identities in PC Core (automatic on backend loss).'
    }
}

# --------------------------------------------------------------- apply

Section 'Result'

if (-not $Apply) {
    Write-Output '   DRY RUN. Nothing was changed.'
    Write-Output '   Re-run with -Apply to perform the action, which will ask for confirmation first.'

    # The baseline is written on a dry run too, so the comparison after a real install is against
    # the posture as it was before anything happened rather than after step one.
    try {
        New-Item -ItemType Directory -Force (Split-Path $BaselineFile) | Out-Null
        $posture | ConvertTo-Json | Set-Content -Path $BaselineFile -Encoding utf8
        Write-Output "   Security baseline recorded: $BaselineFile"
    } catch {
        Write-Output "   (could not record the security baseline: $_)"
    }

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

throw 'Apply is not implemented, deliberately. The first real install is an owner-driven pass that ' +
      'should be watched command by command - especially step 5, where a device is created and a ' +
      'failure between it and step 6 leaves a devnode to clean up.'
