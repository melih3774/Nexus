<#
.SYNOPSIS
    One-time: gives the Nexus Android app a stable, private release-signing key, outside the repository.

.DESCRIPTION
    Android upgrades an app in place - keeping its data, its Keystore keys and therefore its pairing -
    only when the new APK is signed by the same key as the installed one. A different key means
    uninstall, reinstall and pair again.

    Until now the phone app was installed as a debug build, signed with this PC's Android SDK debug key
    (%USERPROFILE%\.android\debug.keystore). That key is a file the SDK silently regenerates if it goes
    missing, which would make every later update impossible to install over the app on the phone.

    By default this ADOPTS that same key - so the app already on the phone accepts the release build
    as an upgrade - by copying it to a Nexus-owned location with its own random password and alias:

        %USERPROFILE%\.nexus\release-signing\nexus-release.p12
        %USERPROFILE%\.nexus\release-signing\signing.properties

    The directory is restricted to the current user. Nothing here is written into the repository, and
    app\build.gradle.kts only reads the properties file's location.

    It never overwrites an existing setup. BACK UP that directory: without it, no future Nexus build can
    be installed over the app on the phone.

.PARAMETER NewKey
    Generate a new key instead of adopting the debug key. Only for a phone that does not have Nexus
    installed yet (or when reinstalling and re-pairing is acceptable).

.EXAMPLE
    .\tools\setup-android-signing.ps1
#>
[CmdletBinding()]
param(
    [switch] $NewKey,
    [string] $Directory = (Join-Path $env:USERPROFILE '.nexus\release-signing')
)

$ErrorActionPreference = 'Stop'

function Find-Keytool {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += (Join-Path $env:JAVA_HOME 'bin\keytool.exe') }
    $candidates += 'C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe'

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    $onPath = Get-Command keytool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw 'keytool.exe not found. Install Android Studio or set JAVA_HOME.'
}

function Get-CertificateSha256([string] $keytool, [string] $store, [string] $password, [string] $alias) {
    $out = & $keytool -list -v -keystore $store -storepass $password -alias $alias 2>&1 | Out-String
    $match = [regex]::Match($out, 'SHA256:\s*([0-9A-F:]+)')
    if (-not $match.Success) { throw "Could not read the certificate from $store." }
    return $match.Groups[1].Value.Replace(':', '').ToLowerInvariant()
}

$properties = Join-Path $Directory 'signing.properties'
$storeName = 'nexus-release.p12'
$store = Join-Path $Directory $storeName
$alias = 'nexus'
$keytool = Find-Keytool

if (Test-Path $properties) {
    $existing = @{}
    Get-Content $properties | Where-Object { $_ -match '^\s*([^#=]+?)\s*=\s*(.*)$' } | ForEach-Object {
        $existing[$Matches[1]] = $Matches[2]
    }

    $existingStore = if ([IO.Path]::IsPathRooted($existing.storeFile)) { $existing.storeFile } else { Join-Path $Directory $existing.storeFile }
    $sha = Get-CertificateSha256 $keytool $existingStore $existing.storePassword $existing.keyAlias

    Write-Output "Nexus release signing is already set up: $properties"
    Write-Output "Certificate SHA-256: $sha"
    Write-Output 'Nothing was changed.'
    return
}

New-Item -ItemType Directory -Force $Directory | Out-Null

# The owner only. Inheritance removed so the key is not readable by other accounts on this PC.
& icacls $Directory /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null

$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = -join ($bytes | ForEach-Object { '{0:x2}' -f $_ })

if ($NewKey) {
    & $keytool -genkeypair -v -storetype PKCS12 -keystore $store -storepass $password -keypass $password `
        -alias $alias -keyalg RSA -keysize 4096 -validity 20000 -dname 'CN=Nexus Release, O=Nexus' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'keytool could not generate the key.' }
}
else {
    $debug = Join-Path $env:USERPROFILE '.android\debug.keystore'
    if (-not (Test-Path $debug)) {
        throw "No Android debug key at $debug to adopt. Use -NewKey (the app on the phone will need reinstalling)."
    }

    Copy-Item $debug $store

    # The SDK's well-known debug password is replaced; the certificate - which is what Android
    # compares - is untouched.
    & $keytool -storepasswd -new $password -keystore $store -storepass android | Out-Null
    if ($LASTEXITCODE -ne 0) { Remove-Item $store -Force; throw 'keytool could not re-protect the key.' }

    & $keytool -changealias -alias androiddebugkey -destalias $alias -keystore $store -storepass $password -keypass $password | Out-Null
    if ($LASTEXITCODE -ne 0) { Remove-Item $store -Force; throw 'keytool could not rename the key.' }
}

@(
    '# Nexus Android release signing. Private: never commit, never copy anywhere public. Back it up.',
    "storeFile=$storeName",
    "storePassword=$password",
    "keyAlias=$alias",
    "keyPassword=$password"
) | Set-Content -Path $properties -Encoding ascii

$sha = Get-CertificateSha256 $keytool $store $password $alias

Write-Output "Nexus release signing set up in $Directory"
Write-Output "Certificate SHA-256: $sha"
if (-not $NewKey) {
    Write-Output 'Adopted from the Android SDK debug key, so the Nexus app already on the phone can be upgraded in place.'
}
Write-Output ''
Write-Output "Back up $Directory. Losing it means the phone app can only be replaced by reinstalling and pairing again."
