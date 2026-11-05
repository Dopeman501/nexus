param([switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$version = '3.1.8-beta.2-d215.1'
$payload = Join-Path $PSScriptRoot 'payload'
$install = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Nexus'
$installedExe = Join-Path $install 'Nexus.exe'
$webRoot = Join-Path $install 'wwwroot'
$manifest = Join-Path $PSScriptRoot 'PAYLOAD-SHA256.txt'

if ((Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion -ne $version) {
    throw "This repair is for installed Nexus $version."
}
foreach ($item in @(Get-Item -LiteralPath $install, $webRoot) + @(Get-ChildItem -LiteralPath $webRoot -Recurse -Force)) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The installed web folder contains a redirected path.'
    }
}

$webFiles = foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([a-f0-9]{64}) \*(wwwroot\\.+)$') { continue }
    $expected = $Matches[1]
    $relative = $Matches[2]
    $source = [IO.Path]::GetFullPath((Join-Path $payload $relative))
    $destination = [IO.Path]::GetFullPath((Join-Path $install $relative))
    if (-not $source.StartsWith((Join-Path $payload 'wwwroot') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $destination.StartsWith($webRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $source).Hash -ne $expected) {
        throw 'A staged web file failed verification.'
    }
    [pscustomobject]@{ Source = $source; Destination = $destination; Hash = $expected }
}
if ($webFiles.Count -lt 2) { throw 'The staged manifest is missing the web bundle.' }

$repair = @($webFiles | Where-Object {
    -not (Test-Path -LiteralPath $_.Destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $_.Destination).Hash -ne $_.Hash
})
if ($CheckOnly) {
    Write-Host "Verified $($webFiles.Count) staged web files. Installed web files to restore: $($repair.Count)."
    return
}

if ($repair.Count -eq 0) {
    Write-Host "All $($webFiles.Count) installed Nexus web files already match the package."
} else {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell as administrator to repair the installed Nexus web files.'
    }
    foreach ($file in $repair) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $file.Destination)) | Out-Null
        [IO.File]::Copy($file.Source, $file.Destination, $true)
    }
    foreach ($file in $webFiles) {
        if ((Get-FileHash -LiteralPath $file.Destination).Hash -ne $file.Hash) {
            throw 'An installed web file failed verification. Check the package before restarting Nexus.'
        }
    }
    Restart-Service -Name NexusService
    Write-Host "Restored $($repair.Count) web files and verified all $($webFiles.Count)."
}

$origin = 'http://127.0.0.1:9400'
$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    $ping = $null
    try { $ping = Invoke-RestMethod "$origin/ping" -UseBasicParsing -TimeoutSec 2 }
    catch { Start-Sleep -Milliseconds 350 }
} until ($ping.version -eq "v$version" -or $timer.Elapsed.TotalSeconds -ge 25)
if ($ping.version -ne "v$version") { throw 'The web files are in place, but NexusService did not respond.' }

$page = Invoke-WebRequest "$origin/" -UseBasicParsing -TimeoutSec 5
$scripts = [regex]::Matches($page.Content, '<script[^>]+src="(/assets/[^" ]+\.js)"')
if ($scripts.Count -eq 0) { throw 'The installed dashboard does not reference its JavaScript entry point.' }
foreach ($script in $scripts) {
    $response = Invoke-WebRequest ($origin + $script.Groups[1].Value) -UseBasicParsing -Method Head -TimeoutSec 5
    if ($response.StatusCode -ne 200 -or $response.Headers['Content-Type'] -notmatch 'javascript') {
        throw 'The installed service did not serve the dashboard script.'
    }
}
Write-Host 'NexusService is running and serving the dashboard JavaScript. Open Nexus again from Start.'
