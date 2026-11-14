#requires -Version 7.0
<# Export committed Git blobs, including recursive submodules, without ignored inputs. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $output) { throw 'Source output already exists.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null

function Read-Git([string]$Root, [string[]]$Arguments) {
    # Archive must retain Git blob bytes, independent of Windows checkout filters.
    $result = & git -c core.autocrlf=false -c core.eol=lf -C $Root @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Git source export failed.' }
    return ($result -join "`n")
}

$queue = [Collections.Generic.Queue[object]]::new()
$queue.Enqueue(@{ Root = $repository; Prefix = 'Jungle-Nexus-source/'; Commit = (Read-Git $repository @('rev-parse','HEAD')).Trim() })
$archive = [IO.Compression.ZipFile]::Open($output, [IO.Compression.ZipArchiveMode]::Create)
$entries = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$sources = @()
try {
    while ($queue.Count) {
        $source = $queue.Dequeue()
        if ((Read-Git $source.Root @('status','--porcelain=v1')) -or
            (Read-Git $source.Root @('rev-parse','HEAD')).Trim() -cne $source.Commit) {
            throw 'Source tree is dirty or differs from its committed pin.'
        }
        $partPath = Join-Path (Split-Path -Parent $output) ([Guid]::NewGuid().ToString('N') + '.source-part.zip')
        $null = Read-Git $source.Root @('archive','--format=zip',"--prefix=$($source.Prefix)","--output=$partPath",$source.Commit)
        $part = [IO.Compression.ZipFile]::OpenRead($partPath)
        try {
            foreach ($entry in $part.Entries | Where-Object Name -ne '') {
                if (!$entries.Add($entry.FullName)) { throw 'Duplicate source archive file.' }
                $destination = $archive.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                $destination.LastWriteTime = $entry.LastWriteTime
                $inputStream = $entry.Open(); $outputStream = $destination.Open()
                try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
            }
        } finally { $part.Dispose() }
        # Only the exact temporary archive created above is removed.
        Remove-Item -LiteralPath $partPath
        $sources += [pscustomobject]@{ Path = $source.Prefix; Commit = $source.Commit }
        $tree = (Read-Git $source.Root @('ls-tree','-r','-z',$source.Commit)).Split([char]0,[StringSplitOptions]::RemoveEmptyEntries)
        foreach ($row in $tree) {
            if ($row -match '^160000 commit ([a-f0-9]{40})\t(.+)$') {
                $pin = $Matches[1]; $relative = $Matches[2]
                if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)') { throw 'Unsafe submodule path.' }
                $queue.Enqueue(@{ Root = Join-Path $source.Root $relative; Prefix = $source.Prefix + $relative + '/'; Commit = $pin })
            }
        }
    }
} finally { $archive.Dispose() }
@{ Archive = [IO.Path]::GetFileName($output); Files = $entries.Count; Sources = $sources } |
    ConvertTo-Json -Depth 5
