#requires -Version 7.0
<#
Wrap an independently verified publish tree in the full Windows installer.
Does not run the application, install it, sign it or contact hosted services.
Use a new output directory; inputs and previous outputs are never removed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$PayloadManifest,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version = '3.1.8-beta.2-d215.23',
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = (Resolve-Path -LiteralPath $PublishDir).Path

function Get-ScopedPath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)|:') {
        throw 'Manifest path is not relative and contained.'
    }
    $path = [IO.Path]::GetFullPath($Relative, $Root)
    if (!$path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Manifest path escaped its root.'
    }
    return $path
}

function Read-VerifiedPayload {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -Force)
    if (@($files | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Redirected payload input.'
    }
    foreach ($line in Get-Content -LiteralPath $PayloadManifest) {
        if ($line -notmatch '^([a-fA-F0-9]{64}) [* ](.+)$') { throw 'Malformed payload manifest.' }
        $hash = $Matches[1]; $relative = $Matches[2]
        $source = Get-ScopedPath $publish $relative
        if (!$seen.Add($source)) { throw 'Duplicate payload manifest path.' }
        if (!(Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source).Hash -ine $hash) { throw "Payload hash mismatch: $relative" }
        if ($relative -match '(^|[\\/])(\.env($|\.)|settings\.json$|profiles\.json$|client-token$)|\.(pdb|log|map)$') {
            throw 'Private state or debug output is not a release input.'
        }
        [pscustomobject]@{ Path = $relative; Source = $source; Sha256 = $hash.ToLowerInvariant() }
    }
    if (@($files | Where-Object { !$_.PSIsContainer }).Count -ne $seen.Count) {
        throw 'Payload contains files outside its manifest.'
    }
}

function Read-Git([string]$Root, [string[]]$Arguments) {
    $result = & git -C $Root @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Git source identity check failed.' }
    return ($result -join "`n")
}

function Replace-Once([string]$Text, [string]$Before, [string]$After) {
    if (($Text.Split($Before, [StringSplitOptions]::None).Count - 1) -ne 1) {
        throw "Installer recipe changed; review the replacement for: $Before"
    }
    return $Text.Replace($Before, $After)
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$records = @(Read-VerifiedPayload)
$exe = Get-ScopedPath $publish 'Nexus.exe'
if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -cne $Version) {
    throw 'Native executable version differs from requested installer version.'
}
$required = @('wwwroot/index.html','overlay/nexus-overlay.exe','overlay/WebView2Loader.dll',
    'openrgb/OpenRGB-headless.exe','tools/ffmpeg/ffmpeg.exe','LICENSE','THIRD-PARTY.md')
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Get-ScopedPath $publish $relative) -PathType Leaf)) {
        throw "Full application runtime missing: $relative"
    }
}
if ($VerifyOnly) { return [pscustomobject]@{ VerifiedFiles = $records.Count; Version = $Version } }
if (!(Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Inno Setup 6 compiler not found.' }
if (Read-Git $repository @('status','--porcelain=v1')) { throw 'Commit reviewed source before packaging.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
$stage = Join-Path $output 'payload'
New-Item -ItemType Directory -Path $stage | Out-Null

# Historical local delivery metadata is replaced by portable, committed provenance.
foreach ($record in $records | Where-Object Path -ne 'COMPARISON-BUILD.json') {
    $target = Get-ScopedPath $stage $record.Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $record.Source -Destination $target
    if ((Get-FileHash -LiteralPath $target).Hash -ine $record.Sha256) { throw 'Staged copy differs.' }
}
$sources = [ordered]@{ umbrella = (Read-Git $repository @('rev-parse','HEAD')).Trim() }
foreach ($component in @('nexus-service','nexus-web','nexus-overlay','nexus-rgb')) {
    $pin = Read-Git $repository @('ls-tree','HEAD',$component)
    $head = (Read-Git (Join-Path $repository $component) @('rev-parse','HEAD')).Trim()
    if ($pin -notmatch ('^160000 commit ' + $head + '\s')) { throw 'Component differs from committed gitlink.' }
    $sources[$component] = $head
}
# Correct the inherited LGPL-only notice; the unchanged FFmpeg binary uses GPL/x264.
Copy-Item -LiteralPath (Join-Path $repository 'nexus-service/THIRD-PARTY.md') -Destination (Join-Path $stage 'THIRD-PARTY.md') -Force
$provenance = [ordered]@{
    Product = 'Jungle Nexus'; Publisher = 'Dopeman501'; Version = $Version
    Repository = 'https://github.com/Dopeman501/nexus'; Sources = $sources
    NativeSha256 = (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
    DashboardIndexSha256 = (Get-FileHash -LiteralPath (Join-Path $stage 'wwwroot/index.html')).Hash.ToLowerInvariant()
    AcceptedPayloadManifestSha256 = (Get-FileHash -LiteralPath $PayloadManifest).Hash.ToLowerInvariant()
    AcceptedPayloadFiles = $records.Count; InstallerSigned = $false
    NoticeCorrections = @('THIRD-PARTY.md: GPL/x264 and source-provenance limit')
    WebView2DownloadIfMissing = $true; InstalledOrHardwareTestedByThisScript = $false
}
$provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'RELEASE.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $stage 'RELEASE.json') -Destination (Join-Path $output 'RELEASE.json')

$recipe = [IO.File]::ReadAllText((Join-Path $repository 'nexus-service/installer/Nexus.iss'))
$recipe = Replace-Once $recipe '#define MyAppName "Nexus"' '#define MyAppName "Jungle Nexus"'
$recipe = Replace-Once $recipe '#define MyAppPublisher "Nexus"' '#define MyAppPublisher "Dopeman501"'
$recipe = Replace-Once $recipe '#define MyAppURL "https://hellonexus.com"' '#define MyAppURL "https://github.com/Dopeman501/nexus"'
# The native --install lifecycle deliberately retains the compatible Nexus path.
$recipe = Replace-Once $recipe 'DefaultDirName={commonpf64}\{#MyAppName}' 'DefaultDirName={commonpf64}\Nexus'
# Keep the page's desktop-shortcut choice while locking the required native path.
$recipe = Replace-Once $recipe '  DesktopShortcutCheck := TNewCheckBox.Create(WizardForm);' @'
  WizardForm.DirEdit.Text := ExpandConstant('{commonpf64}\Nexus');
  WizardForm.DirEdit.Enabled := False;
  WizardForm.DirBrowseButton.Enabled := False;
  DesktopShortcutCheck := TNewCheckBox.Create(WizardForm);
'@
$recipe = Replace-Once $recipe 'WizardSmallImageFile=logo-small.bmp' '; Use the neutral Inno wizard artwork.'
$recipe = Replace-Once $recipe 'SetupIconFile=..\icon.ico' '; Use the neutral Inno setup icon.'
$recipe = Replace-Once $recipe 'OutputBaseFilename=Nexus-Setup' "OutputBaseFilename=Jungle-Nexus-$Version-Setup"
$recipe = Replace-Once $recipe 'OutputDir=output' "OutputDir=$output"
$recipe = Replace-Once $recipe 'Nexus is digitally signed and safe.' 'This community build is unsigned. Verify its source and published SHA-256 before proceeding.'
$recipe = Replace-Once $recipe '#define AntivirusDocsUrl "https://hellonexus.com/docs/troubleshooting/antivirus"' '#define AntivirusDocsUrl "https://github.com/Dopeman501/nexus/issues"'
$recipePath = Join-Path $output 'Jungle-Nexus.iss'
[IO.File]::WriteAllText($recipePath, $recipe, [Text.UTF8Encoding]::new($false))
$numeric = ($Version -split '-')[0] + '.0'
if ((Read-Git $repository @('status','--porcelain=v1')) -or
    (Read-Git $repository @('rev-parse','HEAD')).Trim() -cne $sources.umbrella) {
    throw 'Source changed during staging.'
}
foreach ($component in @('nexus-service','nexus-web','nexus-overlay','nexus-rgb')) {
    if ((Read-Git (Join-Path $repository $component) @('rev-parse','HEAD')).Trim() -cne $sources[$component]) {
        throw 'Component HEAD changed during staging.'
    }
}
& $Compiler "/DPublishDir=$stage" "/DMyAppVersion=$Version" "/DMyAppVersionInfo=$numeric" $recipePath
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $output "Jungle-Nexus-$Version-Setup.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'Compiler produced no installer.' }
$inventory = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/')
    "{0} *{1}" -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
})
$inventory | Set-Content -LiteralPath (Join-Path $output 'PAYLOAD-SHA256.txt') -Encoding utf8NoBOM
Get-Item -LiteralPath $installer | Select-Object Name,Length
