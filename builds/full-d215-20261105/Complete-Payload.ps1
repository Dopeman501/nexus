$ErrorActionPreference = 'Stop'
$stock = 'C:\Program Files\Nexus'
$payload = Join-Path $PSScriptRoot 'payload'
$expected = '3.1.8-beta.2-d215.1'
if ((Get-Item -LiteralPath "$stock\Nexus.exe").VersionInfo.ProductVersion -ne '3.1.8-beta.2') {
    throw 'Installed Nexus no longer matches the selected stock baseline.'
}
if ((Get-Item -LiteralPath "$payload\Nexus.exe").VersionInfo.ProductVersion -ne $expected) {
    throw 'The local D215 service is missing or has the wrong version.'
}

# These release sidecars retain stock Windows features, including the signed
# virtual display driver. The service, dashboard and overlay come from source.
$support = @('angle', 'openrgb', 'pawnio', 'tools', 'vcam', 'vdd')
foreach ($name in $support) {
    $source = Get-Item -LiteralPath (Join-Path $stock $name)
    $items = @(Get-ChildItem -LiteralPath $source.FullName -Recurse -Force)
    if ((@($source) + $items) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw "An installed support file redirects outside $name."
    }
    foreach ($file in $items | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($stock, $file.FullName)
        $target = Join-Path $payload $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw 'A copied support file failed verification.'
        }
    }
}
foreach ($name in @('glfw3.dll', 'PawnIOLib.dll')) {
    Copy-Item -LiteralPath (Join-Path $stock $name) -Destination (Join-Path $payload $name) -Force
}

$required = @('Nexus.exe', 'overlay\nexus-overlay.exe', 'overlay\WebView2Loader.dll', 'wwwroot\index.html',
    'libSkiaSharp.dll', 'openrgb\OpenRGB-headless.exe', 'vcam\NexusVCam.dll', 'vdd\NexusVirtualDisplayHost.exe')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) {
        throw "Required payload file missing: $relative"
    }
}
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
$files | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($payload, $_.FullName)
    "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()) *$relative"
} | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'PAYLOAD-SHA256.txt') -Encoding ascii
"Verified $($files.Count) payload files. Local build version $expected."
