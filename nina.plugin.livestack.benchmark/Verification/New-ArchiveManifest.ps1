param(
    [string]$ArchiveRoot = '\\10.86.10.22\Belzeberg\Astrofotografie',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Stage,
    [long]$MaximumCacheBytes = 48GB
)

$ErrorActionPreference = 'Stop'
$sessions = @(
    '2022-07-14 Veil Panel 1', '2022-07-15 Veil Panel 2', '2022-08-06 M15',
    '2023-05-26_M101', '2024-01-11 Rosette', '2024-12-26 Lowers Nebula',
    '2025-01-10_Leo_Triplet', '2022-11-13 M31', '2023-04-09_M51', '2022-12-16 Caldwell9'
)
$holdout = @('2022-11-13 M31', '2023-04-09_M51', '2022-12-16 Caldwell9')
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if ($destination.StartsWith([IO.Path]::GetFullPath($ArchiveRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verification output must be outside the source archive.'
}
[IO.Directory]::CreateDirectory($destination) | Out-Null

function Read-CaptureHeader([string]$Path) {
    $values = @{}
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ([IO.Path]::GetExtension($Path) -ieq '.xisf') {
            $prefix = [byte[]]::new(16)
            $stream.ReadExactly($prefix)
            $length = [BitConverter]::ToUInt32($prefix, 8)
            if ($length -gt 16MB) { throw "Unreasonably large XISF header: $Path" }
            $bytes = [byte[]]::new($length)
            $stream.ReadExactly($bytes)
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $reader = [Xml.XmlReader]::Create([IO.StringReader]::new([Text.Encoding]::UTF8.GetString($bytes)), $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.Load($reader)
                foreach ($keyword in $document.SelectNodes("//*[local-name()='FITSKeyword']")) {
                    $values[$keyword.GetAttribute('name')] = $keyword.GetAttribute('value').Trim("' ")
                }
                $image = $document.SelectSingleNode("//*[local-name()='Image']")
                if ($image) {
                    $geometry = $image.GetAttribute('geometry').Split(':')
                    $values['WIDTH'] = $geometry[0]
                    $values['HEIGHT'] = $geometry[1]
                }
            } finally { $reader.Dispose() }
        } else {
            # A compressed FITS image can have an empty primary HDU followed by image keywords in a table HDU.
            for ($hdu = 0; $hdu -lt 2; $hdu++) {
                $end = $false
                for ($block = 0; $block -lt 128 -and -not $end; $block++) {
                    $bytes = [byte[]]::new(2880)
                    $stream.ReadExactly($bytes)
                    $text = [Text.Encoding]::ASCII.GetString($bytes)
                    for ($offset = 0; $offset -lt 2880; $offset += 80) {
                        $card = $text.Substring($offset, 80)
                        $key = $card.Substring(0, 8).Trim()
                        if ($key -eq 'END') { $end = $true; break }
                        if ($card[8] -ne '=') { continue }
                        $value = $card.Substring(10).Trim()
                        if ($value.StartsWith("'")) {
                            $match = [regex]::Match($value, "^'((?:[^']|'')*)'")
                            if ($match.Success) { $value = $match.Groups[1].Value.Replace("''", "'").Trim() }
                        } else { $value = $value.Split('/')[0].Trim() }
                        $values[$key] = $value
                    }
                }
                if (-not $end) { throw "FITS header has no END: $Path" }
                if ($values['NAXIS'] -ne '0') { break }
            }
            $values['WIDTH'] = if ($values.ContainsKey('ZNAXIS1')) { $values['ZNAXIS1'] } else { $values['NAXIS1'] }
            $values['HEIGHT'] = if ($values.ContainsKey('ZNAXIS2')) { $values['ZNAXIS2'] } else { $values['NAXIS2'] }
        }
    } finally { $stream.Dispose() }
    return $values
}

$files = foreach ($session in $sessions) {
    Get-ChildItem -LiteralPath (Join-Path $ArchiveRoot $session) -Recurse -File |
        Where-Object { $_.Extension -in '.fits', '.fit', '.fts', '.xisf' -and $_.FullName -match '\\LIGHT\\' } |
        ForEach-Object { [PSCustomObject]@{ Session = $session; File = $_ } }
}
$total = ($files.File | Measure-Object Length -Sum).Sum
if ($Stage -and $total -gt $MaximumCacheBytes) { throw "Corpus exceeds cache budget: $total bytes." }
$records = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$progressFile = Join-Path $destination 'manifest-progress.jsonl'
$writer = [IO.StreamWriter]::new($progressFile, $false, [Text.UTF8Encoding]::new($false))
try {
    foreach ($entry in $files) {
        $source = $entry.File.FullName
        $relative = [IO.Path]::GetRelativePath($ArchiveRoot, $source)
        $inputPath = $source
        if ($Stage) {
            $inputPath = Join-Path (Join-Path $destination 'cache') $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($inputPath)) | Out-Null
            [IO.File]::Copy($source, $inputPath, $true)
        }
        $hash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
        $header = Read-CaptureHeader $inputPath
        $imageType = $header['IMAGETYP']
        $reason = if (-not $seen.Add($hash)) { 'Duplicate content' } elseif ($imageType -and $imageType -notmatch '(?i)LIGHT|SNAPSHOT') { "Image type: $imageType" } else { $null }
        $record = [PSCustomObject]@{
            Session = $entry.Session; Holdout = $entry.Session -in $holdout
            RelativePath = $relative; SourcePath = $source; Path = $inputPath
            Sha256 = $hash; Bytes = $entry.File.Length; ExcludedReason = $reason
            Header = $header
        }
        $records.Add($record)
        $writer.WriteLine(($record | ConvertTo-Json -Depth 5 -Compress))
        $writer.Flush()
        if ($records.Count % 25 -eq 0) { Write-Output "$($records.Count)/$($files.Count) files recorded" }
    }
} finally { $writer.Dispose() }
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
foreach ($group in ($records | Where-Object { -not $_.ExcludedReason } | Group-Object Session)) {
    $ordered = $group.Group | Sort-Object { $_.Header['DATE-OBS'] }, RelativePath
    $ordered.Path | Set-Content -LiteralPath (Join-Path $destination ($group.Name + '.txt')) -Encoding utf8
}
$records | Group-Object Session | ForEach-Object {
    [PSCustomObject]@{ Session = $_.Name; Files = $_.Count; Eligible = @($_.Group | Where-Object { -not $_.ExcludedReason }).Count }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'summary.json') -Encoding utf8
Write-Output "Manifest saved to $destination"
