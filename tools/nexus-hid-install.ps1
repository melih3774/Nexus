<#
.SYNOPSIS
    Inspects the machine and prints the exact Nexus HID driver install or uninstall plan, with the
    real tool paths and commands. It changes nothing: you run the plan, from an elevated PowerShell.

.DESCRIPTION
    This is the whole of install day in one script, so the deliberate pass is a matter of reading
    output and running the printed commands rather than remembering nine of them.

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
    the machine Nexus was developed on, where Secure Boot is off and always has been. Nexus does not
    require Secure Boot and does not change it; what matters is that installing changes nothing, so
    the posture is captured before and compared after rather than measured against a hardcoded
    expectation.

    THE SIGNING KEY IS YOURS

    Nexus publishes no signed driver. Each PC signs the package it built with a key of its own,
    created here on first install (CN=Nexus Driver Signing, RSA 3072, code signing only, the private
    key not exportable) in the installing account's personal store, and trusts that one certificate
    in two Local Machine stores. A driver rebuilt on this PC later is signed with the same key, by
    hand as in the plan's step 2: tools\publish-release.ps1 leaves out a package without a valid
    signature.

.PARAMETER Action
    Inspect (default), Install, Repair, Uninstall.

.PARAMETER Apply
    Reserved. Performing the plan is not automated yet; -Apply refuses and changes nothing. Repair
    stays a plan in any case: a newer driver reaches an installed PC through the Nexus updater
    (docs\updating.md), which updates the existing device in place.

.PARAMETER RemoveSigningKey
    With -Action Uninstall: the plan also deletes the signing key from the personal store. Without
    it the key stays, untrusted, so a later install signs with the same key again.

.EXAMPLE
    .\nexus-hid-install.ps1
    .\nexus-hid-install.ps1 -Action Install            # the exact install plan for this PC
    .\nexus-hid-install.ps1 -Action Uninstall          # the plan to remove the device, packages and trust
#>
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Install', 'Repair', 'Uninstall')]
    [string] $Action = 'Inspect',

    [switch] $Apply,

    [switch] $RemoveSigningKey,

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
$InstanceId = 'NexusHid'

# What devgen creates for /bus ROOT /instanceid NexusHid, and what the updater and PC Core look for.
$DeviceInstance = 'ROOT\DEVGEN\NEXUSHID'

$UmdfServiceKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\NexusHidDriver'

function Section([string] $title) {
    Write-Output ''
    Write-Output "== $title =="
}

function Field([string] $name, $value) {
    Write-Output ("   {0,-28} {1}" -f $name, $value)
}

# --------------------------------------------------------------- WDK tooling

function Find-WdkTool([string] $name) {
    # Building the driver needs the WDK, so installing it can too: devgen creates the root device and
    # signtool signs the catalog. The newest kit version wins.
    $roots = @(
        'C:\Program Files (x86)\Windows Kits\10\Tools',
        'C:\Program Files (x86)\Windows Kits\10\bin'
    )

    foreach ($root in $roots) {
        $hit = Get-ChildItem -Path $root -Recurse -Filter $name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like '*\x64\*' } |
               Sort-Object FullName -Descending |
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

function Test-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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
        Version    = if ($driverVer) { ($driverVer -split ',')[-1].Trim() } else { $null }
        HardwareId = Get-HardwareIdFromInf $inf
        Dll        = $dll
        DllPresent = Test-Path $dll
        DllHash    = if (Test-Path $dll) { (Get-FileHash $dll -Algorithm SHA256).Hash } else { $null }
        Catalog    = $cat
        CatPresent = Test-Path $cat
        CatStatus  = if ($null -ne $signature) { $signature.Status.ToString() } else { 'NotPresent' }
        CatSigner  = if ($null -ne $signature -and $null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
        CatThumb   = if ($null -ne $signature -and $null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
    }
}

function Get-NexusPackages {
    # Every Nexus package in the Driver Store, by published name. pnputil's text is localised, so the
    # blocks are recognised by their values (nexushid.inf, the Nexus provider), not by their labels.
    $text = (& pnputil /enum-drivers 2>$null | Out-String)
    $packages = @()

    foreach ($block in ($text -split '(?:\r?\n){2,}')) {
        if ($block -notmatch '(?im)^\s*[^:\r\n]+:\s*nexushid\.inf\s*$') { continue }
        $oem = [regex]::Match($block, '(?i)\boem\d+\.inf\b').Value
        if ($oem) { $packages += $oem.ToLowerInvariant() }
    }

    return @($packages | Sort-Object -Unique)
}

function Get-InstalledState {
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

    $keys = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
              Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey })

    $packages = Get-NexusPackages

    [pscustomobject]@{
        Packages      = $packages
        InDriverStore = ($packages.Count -gt 0)
        Devices       = $devices
        DeviceCount   = $devices.Count
        TrustedCerts  = $certs
        SigningKeys   = $keys
    }
}

# --------------------------------------------------------------- what the running driver says

# The version the loaded driver reports over IOCTL_NEXUS_HID_GET_VERSION, as the updater asks it
# (src\PcCore.Update\Windows\WindowsHidDriverHost.cs): a bound package is not proof that it runs.
$probeSource = @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class NexusHidRunningVersion {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid cls, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_Interface_ListW(ref Guid cls, string id, char[] buffer, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
    public static string Ask(string deviceInstance) {
        Guid control = new Guid("6d1f4a2e-9c3b-4e7a-b5d8-1a2c3e4f5b60");
        uint length;
        if (CM_Get_Device_Interface_List_SizeW(out length, ref control, deviceInstance, 0) != 0 || length <= 1) return null;
        char[] buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref control, deviceInstance, buffer, length, 0) != 0) return null;
        string path = new string(buffer).Split('\0')[0];
        using (SafeFileHandle handle = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (handle.IsInvalid) return null;
            byte[] reply = new byte[8];
            int returned;
            if (!DeviceIoControl(handle, 0x0022A000, IntPtr.Zero, 0, reply, 8, out returned, IntPtr.Zero) || returned != 8) return null;
            uint v = BitConverter.ToUInt32(reply, 4);
            return string.Format("{0}.{1}.{2}.{3}", v >> 24, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }
    }
}
'@

function Get-RunningVersion([int] $waitSeconds = 0) {
    if (-not ('NexusHidRunningVersion' -as [type])) { Add-Type -TypeDefinition $probeSource }

    $deadline = (Get-Date).AddSeconds($waitSeconds)
    do {
        $version = [NexusHidRunningVersion]::Ask($DeviceInstance)
        if ($version) { return $version }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    return $null
}

# --------------------------------------------------------------- report

$posture = Get-SecurityPosture
$package = Get-PackageState
$installed = Get-InstalledState
$elevated = Test-Elevated

$devgen = Find-WdkTool 'devgen.exe'
$signtool = Find-WdkTool 'signtool.exe'
$pcCore = @(Get-Process -Name 'PcCore.Host' -ErrorAction SilentlyContinue)

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
Field 'Catalog signer' $(if ($package.CatSigner) { "$($package.CatSigner)  $($package.CatThumb)" } else { '- (unsigned)' })

Section 'Tooling (WDK)'
Field 'devgen.exe' $(if ($devgen) { $devgen } else { 'NOT FOUND' })
Field 'signtool.exe' $(if ($signtool) { $signtool } else { 'NOT FOUND' })

Section 'Current install state'
Field 'Nexus packages' $(if ($installed.Packages.Count -eq 0) { 'none' } else { $installed.Packages -join ', ' })
Field 'Nexus devices' $installed.DeviceCount
if ($installed.DeviceCount -gt 0) {
    $installed.Devices | ForEach-Object { Field '  instance' "$($_.InstanceId) ($($_.Status))" }
}
Field 'Trusted Nexus certs' $(if ($installed.TrustedCerts.Count -eq 0) { 'none' } else { ($installed.TrustedCerts | ForEach-Object { "$($_.Store):$($_.Thumbprint)" }) -join ', ' })
Field 'Signing key (personal)' $(if ($installed.SigningKeys.Count -eq 0) { 'none (Install creates one)' } else { ($installed.SigningKeys | ForEach-Object { "$($_.Thumbprint) until $($_.NotAfter.ToString('yyyy-MM-dd'))" }) -join ', ' })
Field 'PC Core running' $(if ($pcCore.Count -gt 0) { "yes (pid $($pcCore.Id -join ', '))" } else { 'no' })
Field 'Elevated' $(if ($elevated) { 'yes' } else { 'no' })

# --------------------------------------------------------------- safety assertions

$blockers = @()

if ($posture.TestSigning -and $Action -ne 'Uninstall') {
    $blockers += 'TESTSIGNING is ON. Nexus does not install under test signing; the whole point is a package that loads on a normally secured machine.'
}

if ($Action -in @('Install', 'Repair')) {
    if (-not $package.InfPresent) { $blockers += "INF missing from the canonical package: $($package.Inf). Run tools\validate-driver-package.ps1 first." }
    if (-not $package.DllPresent) { $blockers += 'Driver DLL missing. Run tools\validate-driver-package.ps1 first.' }
    if ($package.InfPresent -and -not $package.Stamped) { $blockers += 'INF is not stamped. Run tools\validate-driver-package.ps1.' }
    if ($package.InfPresent -and -not $package.HardwareId) { $blockers += 'Hardware ID could not be read from the INF.' }
    if (-not $package.CatPresent) { $blockers += 'No catalog in the canonical package. Run tools\validate-driver-package.ps1.' }
}

if ($Action -eq 'Install') {
    if (-not $devgen) { $blockers += 'devgen.exe was not found. Install the WDK (10.0.26100); a root-enumerated device cannot be created without it.' }
    if (-not $signtool) { $blockers += 'signtool.exe was not found. Install the Windows SDK / WDK (10.0.26100).' }

    if ($installed.DeviceCount -gt 0) {
        $blockers += "A Nexus device already exists ($($installed.DeviceCount)). Installing again would create a duplicate root devnode publishing a second set of HID collections. Newer drivers arrive through the Nexus updater; to start over, follow the -Action Uninstall plan first."
    }
}

if ($Action -eq 'Uninstall' -and $pcCore.Count -gt 0) {
    $blockers += 'PC Core is running and holds the Nexus HID device open, so Windows could only remove it at the next restart. Exit Nexus from its tray icon first.'
}

if ($Apply -and $Action -in @('Install', 'Uninstall') -and -not $elevated) {
    $blockers += 'Not elevated. -Apply changes the Driver Store and what this PC trusts; run it from an elevated PowerShell.'
}

if ($blockers.Count -gt 0) {
    Section 'Blockers'
    $blockers | ForEach-Object { Write-Output "   - $_" }
}

# --------------------------------------------------------------- plans

Section "Plan: $Action"

$hwid = $package.HardwareId
$devgenText = if ($devgen) { "& '$devgen'" } else { 'devgen' }
$signtoolText = if ($signtool) { "& '$signtool'" } else { 'signtool' }

switch ($Action) {
    'Inspect' {
        Write-Output '   Nothing to do. This is a read-only report.'
    }

    'Install' {
        Write-Output '   Run these in ONE elevated PowerShell, in order; each step uses $cert from step 1.'
        Write-Output ''
        $existingKey = $installed.SigningKeys | Where-Object { $_.NotAfter -gt (Get-Date).AddDays(30) } |
                       Sort-Object NotAfter -Descending | Select-Object -First 1
        if ($existingKey) {
            Write-Output "   1. The signing key already exists ($($existingKey.Thumbprint)); use it:"
            Write-Output "        `$cert = Get-Item Cert:\CurrentUser\My\$($existingKey.Thumbprint)"
        } else {
            Write-Output '   1. Create the signing key (kept on this PC; the private key cannot be exported):'
            Write-Output "        `$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject '$CertSubject' ``"
            Write-Output '            -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature `'
            Write-Output '            -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)'
            Write-Output '      Code Signing (1.3.6.1.5.5.7.3.3) is the only purpose it has.'
        }
        Write-Output ''
        Write-Output '   2. Sign the catalog IN PLACE, in the canonical package, with exactly that key:'
        Write-Output "        $signtoolText sign /fd SHA256 /s My /sha1 `$cert.Thumbprint '$($package.Catalog)'"
        Write-Output '      No timestamp, deliberately: a timestamp exists so a signature outlives its'
        Write-Output '      certificate, which matters for distribution and not for one machine that can'
        Write-Output '      re-sign at will.'
        Write-Output ''
        Write-Output '   3. Trust exactly that certificate, in exactly two Local Machine stores. This is the one'
        Write-Output '      step that changes what this computer trusts, so read what you are trusting first:'
        Write-Output '        $cert | Format-List Subject, Thumbprint, NotAfter'
        Write-Output '        Export-Certificate -Cert $cert -FilePath "$env:TEMP\nexus-driver-signing.cer" -Type CERT'
        Write-Output '        Import-Certificate -FilePath "$env:TEMP\nexus-driver-signing.cer" -CertStoreLocation Cert:\LocalMachine\Root'
        Write-Output '        Import-Certificate -FilePath "$env:TEMP\nexus-driver-signing.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPublisher'
        Write-Output '        Remove-Item "$env:TEMP\nexus-driver-signing.cer"'
        Write-Output '      Root lets the chain validate; TrustedPublisher lets PnP install without prompting.'
        Write-Output ''
        Write-Output '   4. Verify before installing:'
        Write-Output "        Get-AuthenticodeSignature '$($package.Catalog)'   # expect Status = Valid"
        Write-Output ''
        Write-Output '   5. CREATE THE ROOT DEVICE. This step is not optional:'
        Write-Output "        $devgenText /add /bus ROOT /hardwareid `"$hwid`" /instanceid $InstanceId"
        Write-Output ''
        Write-Output '      pnputil /add-driver /install alone does NOT create a devnode. It stages the'
        Write-Output '      package and binds it to devices that already exist; a root-enumerated device'
        Write-Output '      has no bus to enumerate it, so nothing appears and the install looks like it'
        Write-Output '      silently did nothing. The device has to be generated first.'
        Write-Output ''
        Write-Output '   6. Add and install the package:'
        Write-Output "        pnputil /add-driver '$($package.Inf)' /install"
        Write-Output ''
        Write-Output '   7. ROLLBACK POINT. If step 5 succeeded and step 6 fails, or the device does not'
        Write-Output '      start, the devnode exists with no working driver. Remove it, and the package if'
        Write-Output '      it was added (pnputil /delete-driver <oem#.inf> /uninstall), before retrying:'
        Write-Output "        pnputil /remove-device $DeviceInstance"
        Write-Output '      Leaving it behind is what produces duplicates on the next attempt.'
        Write-Output ''
        Write-Output '   8. Confirm the posture is UNCHANGED from the captured baseline:'
        Write-Output "        baseline: $(Format-Posture $posture)"
        Write-Output '      Not "Secure Boot ON" - unchanged. Any difference means the install did'
        Write-Output '      something it should not have, and it should be rolled back.'
        Write-Output ''
        Write-Output '   9. Verify that the driver RUNS, not only that it is bound: the device started with'
        Write-Output "      no problem code, the stack WUDFRd -> vhf, and the loaded driver answering $($package.Version)"
        Write-Output '      over IOCTL_NEXUS_HID_GET_VERSION. Then start Nexus: its log says'
        Write-Output '      "Virtual HID: Ready - Nexus HID ... published 3 device(s)" (mouse, keyboard, touch).'
    }

    'Repair' {
        Write-Output "   1. Compare the installed package version against the package's ($($package.Version))."
        Write-Output '   2. A newer driver reaches an installed PC through the Nexus updater, which updates'
        Write-Output '      the existing device in place, verifies that the new driver runs, and rolls back'
        Write-Output '      (tools\publish-release.ps1 -Install; docs\updating.md). By hand, the same:'
        Write-Output "        pnputil /add-driver '$($package.Inf)' /install"
        Write-Output '      Never create a second device.'
        Write-Output "   3. Existing Nexus devices: $($installed.DeviceCount). More than one means a"
        Write-Output '      previous install left a devnode behind; remove the extras with pnputil /remove-device.'
        Write-Output '   4. To start over completely: the -Action Uninstall plan, then the -Action Install plan.'
    }

    'Uninstall' {
        Write-Output '   1. PC Core must not be running: it holds the device, and a held device is only'
        Write-Output '      removed at the next restart. Exit Nexus from its tray icon first.'
        Write-Output '   2. Remove the device, by instance id (its mouse, keyboard and touch go with it):'
        if ($installed.DeviceCount -gt 0) {
            $installed.Devices | Where-Object { $_.InstanceId -like 'ROOT\*' } |
                ForEach-Object { Write-Output "        pnputil /remove-device $($_.InstanceId)" }
        } else {
            Write-Output '        (none present)'
        }
        Write-Output '   3. Remove every Nexus package from the Driver Store:'
        if ($installed.Packages.Count -gt 0) {
            $installed.Packages | ForEach-Object { Write-Output "        pnputil /delete-driver $_ /uninstall" }
        } else {
            Write-Output '        (none present)'
        }
        Write-Output '   4. Remove the certificate from BOTH stores - uninstall that leaves the key'
        Write-Output '      trusted leaves the machine trusting something for no reason:'
        Write-Output "        Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |"
        Write-Output "            Where-Object Subject -eq '$CertSubject' | Remove-Item"
        if ($RemoveSigningKey -and $installed.SigningKeys.Count -gt 0) {
            Write-Output '   5. Delete the signing key (not elevated: it is in your own store):'
            $installed.SigningKeys | ForEach-Object { Write-Output "        Remove-Item Cert:\CurrentUser\My\$($_.Thumbprint) -DeleteKey" }
        } else {
            Write-Output '   5. The signing key in Cert:\CurrentUser\My stays, untrusted, for a later install'
            Write-Output '      (-RemoveSigningKey adds deleting it to this plan).'
        }
        Write-Output '   6. PC Core falls back to injected input the next time it starts.'
    }
}

# --------------------------------------------------------------- apply

Section 'Result'

if (-not $Apply) {
    Write-Output '   DRY RUN. Nothing was changed.'
    if ($Action -in @('Install', 'Uninstall')) {
        Write-Output '   Run the steps above from an elevated PowerShell, checking each result before the next.'
    }

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

if ($Action -eq 'Inspect') {
    Write-Output '   Inspect changes nothing; -Apply has no effect.'
    return
}

if ($Action -eq 'Repair') {
    Write-Output '   Repair is a plan, not an action: driver updates go through the Nexus updater, which'
    Write-Output '   checks that the new driver runs and rolls back if it does not. Nothing was changed.'
    return
}

if ($blockers.Count -gt 0) {
    Write-Output '   REFUSED. Resolve the blockers above first. Nothing was changed.'
    Write-Output '   This script will not weaken Secure Boot, HVCI, TESTSIGNING or UAC to make an install succeed.'
    exit 1
}

# Performing the plan is deliberately not automated yet: every step above is printed with its exact
# command and tool path, and is run by hand, from an elevated PowerShell, watching each result -
# especially step 5, where a device is created and a failure between it and step 6 leaves a devnode
# to remove (step 7).
Write-Output "   -Apply does not perform $Action yet. Run the plan above step by step from an elevated PowerShell."
Write-Output '   Nothing was changed.'
exit 1
