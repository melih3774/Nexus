<#
.SYNOPSIS
    Builds and publishes a Nexus release into the local release store that PC Core and the phone
    update from. With -Install, also updates this PC straight away.

.DESCRIPTION
    change code -> .\tools\publish-release.ps1 -Install -> phone shows "Nexus x.y.z available"

    What it does, in order:
      1. Reads the versions: product (Directory.Build.props), SecureCapture (its csproj), HID driver
         (the signed canonical package in artifacts\NexusHidPackage).
      2. Builds PC Core, the input broker and the updater (dotnet publish), and SecureCapture.
      3. Builds the signed Android release APK - only if the Android sources changed since the last
         release; otherwise the previous APK is carried forward unchanged, so the phone is not offered
         an "update" that changes nothing.
      4. Refuses to publish a changed SecureCapture or driver under an unchanged version, and an APK
         signed by a different key than the last release's.
      5. Hashes every file, writes manifest.json, moves the release into
         %LOCALAPPDATA%\Nexus\Releases\<release>, and only then points latest.json at it.
      6. Keeps the newest -Keep releases.

    Nothing is uploaded anywhere and nothing is written into the repository.

.PARAMETER Install
    After publishing, run the new release's updater against this PC (the same thing the tray's
    "Install update" does) and report how it went.

.PARAMETER SkipAndroid
    Do not build the APK; carry the previous release's forward (or ship none).

.PARAMETER Force
    Publish even when nothing changed since the previous release.

.EXAMPLE
    .\tools\publish-release.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch] $Install,
    [switch] $SkipAndroid,
    [switch] $Force,
    [string] $ReleaseRoot = (Join-Path $env:LOCALAPPDATA 'Nexus\Releases'),
    [int] $Keep = 3
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding $false

function Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Note([string] $text) { Write-Host "   $text" }

function Invoke-Native([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

function Get-Sha256([string] $path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# A hash of a component's source inputs: every tracked or untracked-but-not-ignored file under the
# given paths, by path and content. Used to notice "this changed" without trusting build output bytes.
function Get-SourceFingerprint([string[]] $paths) {
    $files = @(git -C $repo ls-files -co --exclude-standard -- $paths | Sort-Object -Unique)
    $sha = [Security.Cryptography.SHA256]::Create()

    foreach ($file in $files) {
        $full = Join-Path $repo $file
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }

        $name = [Text.Encoding]::UTF8.GetBytes($file + "`n")
        [void] $sha.TransformBlock($name, 0, $name.Length, $null, 0)

        $content = [IO.File]::ReadAllBytes($full)
        [void] $sha.TransformBlock($content, 0, $content.Length, $null, 0)
    }

    [void] $sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
    return -join ($sha.Hash | ForEach-Object { '{0:x2}' -f $_ })
}

function Get-FileEntries([string] $directory) {
    $root = (Resolve-Path -LiteralPath $directory).Path.TrimEnd('\') + '\'

    @(Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered] @{
            path   = $_.FullName.Substring($root.Length).Replace('\', '/')
            sha256 = Get-Sha256 $_.FullName
            size   = $_.Length
        }
    })
}

function Read-Previous {
    $pointer = Join-Path $ReleaseRoot 'latest.json'
    if (-not (Test-Path $pointer)) { return $null }

    try {
        $latest = Get-Content -Raw $pointer | ConvertFrom-Json
        $manifestPath = Join-Path $ReleaseRoot $latest.manifest

        if ((Get-Sha256 $manifestPath) -ne $latest.manifest_sha256.ToLowerInvariant()) {
            Write-Warning 'The current latest.json does not match its manifest; treating this as the first release.'
            return $null
        }

        return [pscustomobject] @{
            Manifest  = (Get-Content -Raw $manifestPath | ConvertFrom-Json)
            Directory = Split-Path -Parent $manifestPath
        }
    }
    catch {
        Write-Warning "The previous release could not be read ($_); treating this as the first release."
        return $null
    }
}

function Get-Property($object, [string] $name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# ------------------------------------------------------------------------------------- state location

# A process started from inside a packaged (MSIX) app - its terminal, and this script when run there -
# is shown that app's private copy of any AppData folder the app created first, under
# %LOCALAPPDATA%\Packages\<package>\LocalCache\Local. A release published into that copy, and an update
# recorded there, are invisible to Nexus started from the Start Menu: its next restart comes up as if
# neither happened. PcCore.Updater and PC Core refuse the same situation (PcCore.Update AppDataRedirection).
function Get-DirectoryId([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { return $null }
    $answer = & fsutil file queryFileID $path 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return [regex]::Match(($answer -join ' '), '0x[0-9a-fA-F]+').Value
}

$stateFolder = Join-Path $env:LOCALAPPDATA 'Nexus'
$releasesInState = ([IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\') + '\').StartsWith($stateFolder + '\', [StringComparison]::OrdinalIgnoreCase)

if ($Install -or $releasesInState) {
    $stateId = Get-DirectoryId $stateFolder

    foreach ($package in @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue)) {
        $copy = Join-Path $package.FullName 'LocalCache\Local\Nexus'
        if ($stateId -and (Get-DirectoryId $copy) -eq $stateId) {
            throw "This shell runs inside $($package.Name), and Windows shows it a private copy of $stateFolder ($copy). " +
                  'A release published or installed from here would be invisible to Nexus started from the Start Menu. ' +
                  "Run this from a terminal outside $($package.Name), or move what is needed out of the copy into $stateFolder and delete the copy."
        }
    }
}

# --------------------------------------------------------------------------------------------- versions

Step 'Versions'

[xml] $props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ } | Select-Object -First 1)
$minAndroid = [int] ($props.Project.PropertyGroup | ForEach-Object { $_.NexusMinAndroidVersionCode } | Where-Object { $_ } | Select-Object -First 1)

[xml] $secureProject = Get-Content (Join-Path $repo 'src\PcCore.SecureCapture\PcCore.SecureCapture.csproj')
$secureVersion = ($secureProject.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ } | Select-Object -First 1)

$previous = Read-Previous
$previousRelease = if ($previous) { [int] $previous.Manifest.release } else { 0 }

# Release numbers double as the Android versionCode, and Android will not install over a build with a
# higher one. 23 is the highest hand-numbered build ever put on the phone — the AI Assistant branch was
# validated there as 21, 22 and 23 — so a release has to pass that, not just the last release.
$release = [Math]::Max($previousRelease, 23) + 1

Note "Nexus $version, release $release (previous: $(if ($previous) { $previousRelease } else { 'none' }))"
Note "SecureCapture $secureVersion; oldest Android build served: $minAndroid"

$work = Join-Path $ReleaseRoot ".staging-$release"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

try {
    # ----------------------------------------------------------------------------------------- PC Core

    Step 'PC Core'

    $pcDir = Join-Path $work 'pccore'
    foreach ($project in 'src\PcCore.Host', 'src\PcCore.InputBroker', 'src\PcCore.Updater') {
        Note "dotnet publish $project"
        Invoke-Native "Publishing $project" { dotnet publish (Join-Path $repo $project) -c Release -o $pcDir --nologo -v q }
    }

    # IIS configuration the SDK adds to every web project. PC Core is not hosted by IIS.
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $pcDir 'web.config')

    foreach ($required in 'PcCore.Host.exe', 'PcCore.InputBroker.dll', 'PcCore.Updater.exe', 'PcCore.Update.dll') {
        if (-not (Test-Path (Join-Path $pcDir $required))) { throw "The PC Core build has no $required." }
    }

    $pcFiles = Get-FileEntries $pcDir

    # ------------------------------------------------------------------------------------ SecureCapture

    Step 'SecureCapture'

    $secureDir = Join-Path $work 'securecapture'
    Invoke-Native 'Publishing SecureCapture' { dotnet publish (Join-Path $repo 'src\PcCore.SecureCapture') -c Release -o $secureDir --nologo -v q }
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $secureDir 'web.config')

    $secureFingerprint = Get-SourceFingerprint @('src/PcCore.SecureCapture')
    $previousSecure = Get-Property (Get-Property $previous.Manifest 'components') 'secure_capture'

    if ($previousSecure -and $previousSecure.version -eq $secureVersion -and $previousSecure.fingerprint -and $previousSecure.fingerprint -ne $secureFingerprint) {
        throw "SecureCapture changed since release $previousRelease but is still version $secureVersion. " +
              'Bump <VersionPrefix> in src\PcCore.SecureCapture\PcCore.SecureCapture.csproj - an unchanged version is never installed.'
    }

    Note "version $secureVersion$(if ($previousSecure -and $previousSecure.version -eq $secureVersion) { ' (unchanged)' } else { '' })"

    # --------------------------------------------------------------------------------------- HID driver

    Step 'HID driver'

    $driverComponent = $null
    $package = Join-Path $repo 'artifacts\NexusHidPackage'
    $inf = Join-Path $package 'NexusHid.inf'
    $catalog = Join-Path $package 'nexushid.cat'

    if ((Test-Path $inf) -and (Test-Path $catalog) -and (Test-Path (Join-Path $package 'NexusHidDriver.dll'))) {
        $signature = Get-AuthenticodeSignature $catalog
        $driverVersion = ((Select-String -Path $inf -Pattern '^\s*DriverVer\s*=' | Select-Object -First 1).Line -split ',')[-1].Trim()

        if ($signature.Status -ne 'Valid') {
            Write-Warning "The driver catalog signature is '$($signature.Status)'; the driver is left out of this release."
        }
        else {
            $driverDir = Join-Path $work 'driver'
            New-Item -ItemType Directory -Force $driverDir | Out-Null
            Copy-Item (Join-Path $package '*') $driverDir

            $driverFingerprint = Get-SourceFingerprint @('src/driver/NexusHid')
            $previousDriver = Get-Property (Get-Property $previous.Manifest 'components') 'hid_driver'

            if ($previousDriver -and $previousDriver.version -eq $driverVersion) {
                $sameFiles = (@($previousDriver.files | ForEach-Object { "$($_.path)=$($_.sha256)" }) -join ';') -eq (@(Get-FileEntries $driverDir | ForEach-Object { "$($_.path)=$($_.sha256)" }) -join ';')

                if ($previousDriver.fingerprint -ne $driverFingerprint -or -not $sameFiles) {
                    throw "The HID driver changed since release $previousRelease but is still version $driverVersion. " +
                          'Rebuild and re-sign the package with a new DriverVer (tools\validate-driver-package.ps1).'
                }
            }

            $driverComponent = [ordered] @{
                version     = $driverVersion
                fingerprint = $driverFingerprint
                directory   = 'driver'
                files       = Get-FileEntries $driverDir
            }

            Note "version $driverVersion, catalog signed by $($signature.SignerCertificate.Subject)"
        }
    }
    else {
        Note 'no signed package in artifacts\NexusHidPackage; left out of this release'
    }

    # ------------------------------------------------------------------------------------------ Android

    Step 'Android'

    $androidComponent = $null
    $androidFingerprint = Get-SourceFingerprint @('android/Nexus', 'shared/protocol', 'Directory.Build.props')
    $previousAndroid = Get-Property (Get-Property $previous.Manifest 'components') 'android'
    $previousApk = if ($previousAndroid) { Join-Path $previous.Directory $previousAndroid.file } else { $null }
    $androidDir = Join-Path $work 'android'

    $reuse = $previousAndroid -and (Test-Path $previousApk) -and ($SkipAndroid -or $previousAndroid.fingerprint -eq $androidFingerprint)

    if ($reuse) {
        New-Item -ItemType Directory -Force $androidDir | Out-Null
        Copy-Item $previousApk $androidDir

        $androidComponent = [ordered] @{
            version_name        = $previousAndroid.version_name
            version_code        = [int] $previousAndroid.version_code
            file                = $previousAndroid.file
            sha256              = Get-Sha256 (Join-Path $work $previousAndroid.file)
            size                = (Get-Item (Join-Path $work $previousAndroid.file)).Length
            signing_cert_sha256 = $previousAndroid.signing_cert_sha256
            fingerprint         = $previousAndroid.fingerprint
            min_sdk             = [int] $previousAndroid.min_sdk
        }

        Note "unchanged: carrying Nexus $($previousAndroid.version_name) ($($previousAndroid.version_code)) forward"
    }
    elseif ($SkipAndroid) {
        Note 'skipped, and no previous APK to carry forward'
    }
    else {
        $signing = Join-Path $env:USERPROFILE '.nexus\release-signing\signing.properties'
        if ($env:NEXUS_SIGNING_PROPERTIES) { $signing = $env:NEXUS_SIGNING_PROPERTIES }

        if (-not (Test-Path $signing)) {
            throw 'The Android release key is not set up. Run tools\setup-android-signing.ps1 once.'
        }

        if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr' }

        $sdk = $env:ANDROID_HOME
        $localProperties = Join-Path $repo 'android\Nexus\local.properties'
        if (-not $sdk -and (Test-Path $localProperties)) {
            $line = Select-String -Path $localProperties -Pattern '^sdk.dir=' | Select-Object -First 1
            if ($line) { $sdk = ($line.Line -replace '^sdk.dir=', '') -replace '\\:', ':' -replace '\\\\', '\' }
        }
        if (-not $sdk) { throw 'The Android SDK was not found (ANDROID_HOME or android\Nexus\local.properties).' }

        $buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version] ($_.Name -replace '[^0-9.].*$', '') } | Select-Object -Last 1
        $apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
        $aapt2 = Join-Path $buildTools.FullName 'aapt2.exe'

        Note "gradlew :app:assembleRelease (versionCode $release, versionName $version)"
        Push-Location (Join-Path $repo 'android\Nexus')
        try {
            Invoke-Native 'The Android release build' { .\gradlew.bat :app:assembleRelease "-PnexusVersionCode=$release" "-PnexusVersionName=$version" --console=plain -q }
        }
        finally {
            Pop-Location
        }

        $built = Join-Path $repo 'android\Nexus\app\build\outputs\apk\release\app-release.apk'
        if (-not (Test-Path $built)) { throw 'The release build produced no signed APK (app-release.apk). Is the signing key set up?' }

        $certs = (& $apksigner verify --print-certs $built | Out-String)
        if ($LASTEXITCODE -ne 0) { throw 'apksigner could not verify the APK.' }
        $cert = [regex]::Match($certs, 'certificate SHA-256 digest:\s*([0-9a-f]{64})').Groups[1].Value

        if (-not $cert) { throw 'The APK signer certificate could not be read.' }

        if ($previousAndroid -and $previousAndroid.signing_cert_sha256 -ne $cert) {
            throw "This APK is signed by $cert, but release $previousRelease was signed by $($previousAndroid.signing_cert_sha256). " +
                  'Android would refuse it as an update of the installed app.'
        }

        $badging = (& $aapt2 dump badging $built 2>$null | Out-String)
        if ($badging -notmatch "package: name='dev.nexus.control' versionCode='$release' versionName='$([regex]::Escape($version))'") {
            throw 'The APK does not carry the expected package name and version.'
        }
        $minSdk = [int] ([regex]::Match($badging, "minSdkVersion:'(\d+)'").Groups[1].Value)

        $apkName = "nexus-$version-$release.apk"
        New-Item -ItemType Directory -Force $androidDir | Out-Null
        Copy-Item $built (Join-Path $androidDir $apkName)

        $androidComponent = [ordered] @{
            version_name        = $version
            version_code        = $release
            file                = "android/$apkName"
            sha256              = Get-Sha256 (Join-Path $androidDir $apkName)
            size                = (Get-Item (Join-Path $androidDir $apkName)).Length
            signing_cert_sha256 = $cert
            fingerprint         = $androidFingerprint
            min_sdk             = $minSdk
        }

        Note "built Nexus $version ($release), $([math]::Round($androidComponent.size / 1MB, 1)) MB, signer $($cert.Substring(0, 16))..."
    }

    # ------------------------------------------------------------------------------------ anything new?

    $previousPc = Get-Property (Get-Property $previous.Manifest 'components') 'pc_core'
    $pcChanged = -not $previousPc -or ((@($previousPc.files | ForEach-Object { "$($_.path)=$($_.sha256)" }) -join ';') -ne (@($pcFiles | ForEach-Object { "$($_.path)=$($_.sha256)" }) -join ';'))
    $secureChanged = -not $previousSecure -or $previousSecure.version -ne $secureVersion
    $driverChanged = ($null -ne $driverComponent) -and (-not (Get-Property (Get-Property $previous.Manifest 'components') 'hid_driver') -or $previous.Manifest.components.hid_driver.version -ne $driverComponent.version)
    $androidChanged = ($null -ne $androidComponent) -and (-not $previousAndroid -or [int] $previousAndroid.version_code -ne $androidComponent.version_code)

    Step 'Summary'
    Note ("PC Core        {0}" -f $(if ($pcChanged) { 'changed' } else { 'unchanged' }))
    Note ("SecureCapture  {0} {1}" -f $secureVersion, $(if ($secureChanged) { 'changed' } else { 'unchanged' }))
    Note ("HID driver     {0}" -f $(if ($driverComponent) { "$($driverComponent.version) $(if ($driverChanged) { 'changed' } else { 'unchanged' })" } else { 'not included' }))
    Note ("Android        {0}" -f $(if ($androidComponent) { "$($androidComponent.version_name) ($($androidComponent.version_code)) $(if ($androidChanged) { 'changed' } else { 'unchanged' })" } else { 'not included' }))

    if ($previous -and -not $Force -and -not ($pcChanged -or $secureChanged -or $driverChanged -or $androidChanged)) {
        Note "Nothing changed since release $previousRelease; nothing was published. (-Force publishes anyway.)"
        Remove-Item -Recurse -Force $work
        $work = $null
    }
    else {
        # -------------------------------------------------------------------------------------- manifest

        $commit = (git -C $repo rev-parse HEAD)
        $dirty = [bool] (git -C $repo status --porcelain)

        $components = [ordered] @{
            pc_core        = [ordered] @{ version = $version; directory = 'pccore'; files = $pcFiles }
            secure_capture = [ordered] @{ version = $secureVersion; fingerprint = $secureFingerprint; directory = 'securecapture'; files = (Get-FileEntries $secureDir) }
        }
        if ($driverComponent) { $components.hid_driver = $driverComponent }
        if ($androidComponent) { $components.android = $androidComponent }

        $manifest = [ordered] @{
            schema           = 1
            product          = 'Nexus'
            version          = $version
            release          = $release
            published_at_utc = (Get-Date).ToUniversalTime().ToString('o')
            commit           = $commit
            dirty            = $dirty
            compatibility    = [ordered] @{ protocol_version = 1; min_android_version_code = $minAndroid }
            components       = $components
        }

        [IO.File]::WriteAllText((Join-Path $work 'manifest.json'), ($manifest | ConvertTo-Json -Depth 10), $utf8)

        $final = Join-Path $ReleaseRoot $release
        if (Test-Path $final) { Remove-Item -Recurse -Force $final }
        Move-Item $work $final
        $work = $null

        # The pointer is the last thing written: until this moment the release does not exist.
        $pointer = [ordered] @{
            release         = $release
            version         = $version
            manifest        = "$release/manifest.json"
            manifest_sha256 = Get-Sha256 (Join-Path $final 'manifest.json')
        }

        $pointerPath = Join-Path $ReleaseRoot 'latest.json'
        [IO.File]::WriteAllText("$pointerPath.tmp", ($pointer | ConvertTo-Json), $utf8)
        Move-Item -Force "$pointerPath.tmp" $pointerPath

        # Older releases, beyond the newest few. latest.json never points at one of these.
        Get-ChildItem $ReleaseRoot -Directory | Where-Object { $_.Name -match '^\d+$' } |
            Sort-Object { [int] $_.Name } -Descending | Select-Object -Skip $Keep |
            ForEach-Object { Remove-Item -Recurse -Force $_.FullName }

        Step "Published Nexus $version (release $release)"
        Note $final
        if ($dirty) { Note 'Built from a working tree with uncommitted changes.' }
    }
}
finally {
    if ($work -and (Test-Path $work)) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------------------------------- install

if ($Install) {
    Step 'Updating this PC'

    $load = Get-Content -Raw (Join-Path $ReleaseRoot 'latest.json') | ConvertFrom-Json
    $current = Join-Path $ReleaseRoot $load.release
    $manifest = Get-Content -Raw (Join-Path $current 'manifest.json') | ConvertFrom-Json

    $runner = Join-Path $env:LOCALAPPDATA "Nexus\update\runner\$($load.release)"
    New-Item -ItemType Directory -Force $runner | Out-Null

    foreach ($name in 'PcCore.Updater.exe', 'PcCore.Updater.dll', 'PcCore.Updater.deps.json', 'PcCore.Updater.runtimeconfig.json', 'PcCore.Update.dll') {
        $entry = $manifest.components.pc_core.files | Where-Object { $_.path -eq $name }
        $target = Join-Path $runner $name
        Copy-Item (Join-Path $current "pccore\$name") $target -Force
        if ((Get-Sha256 $target) -ne $entry.sha256) { throw "$name does not match the release manifest." }
    }

    $started = Get-Date
    # Not -Wait: in Windows PowerShell that waits for the whole process tree, and the updater's last
    # act is to start PC Core, which keeps running.
    $process = Start-Process -FilePath (Join-Path $runner 'PcCore.Updater.exe') -ArgumentList '--apply', '--release', $load.release -PassThru
    $null = $process.Handle
    $process.WaitForExit()
    $status = Get-Content -Raw (Join-Path $env:LOCALAPPDATA 'Nexus\update\status.json') -ErrorAction SilentlyContinue | ConvertFrom-Json

    Get-Content (Join-Path $env:LOCALAPPDATA 'Nexus\update\updater.log') -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}' -and [datetime]::ParseExact($_.Substring(0, 19), 'yyyy-MM-dd HH:mm:ss', $null) -ge $started.AddSeconds(-1) } |
        ForEach-Object { Note $_ }

    Write-Host ''
    if ($process.ExitCode -eq 0) {
        Write-Host "   $($status.message)" -ForegroundColor Green
    }
    else {
        Write-Host "   Update did not complete (exit $($process.ExitCode)): $($status.message)" -ForegroundColor Yellow
        exit $process.ExitCode
    }
}
