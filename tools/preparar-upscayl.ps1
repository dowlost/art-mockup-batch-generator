param()

$ErrorActionPreference = 'Stop'
$revision = '4f39acfc6f88260d105920a64deff8431d5e1544'
$url = "https://codeload.github.com/upscayl/upscayl/zip/$revision"
$target = Join-Path $PSScriptRoot 'upscayl'
$archive = Join-Path ([IO.Path]::GetTempPath()) ("upscayl-$([Guid]::NewGuid().ToString('N')).zip")
$models = @('upscayl-standard-4x', 'hfa2k-4x', 'digital-art-4x', 'remacri-4x', 'ultramix-balanced-4x', 'ultrasharp-4x', 'upscayl-lite-4x')
$files = [ordered]@{
    'resources/win/bin/upscayl-bin.exe' = 'bin/upscayl-bin.exe'
    'resources/win/bin/vcomp140.dll' = 'bin/vcomp140.dll'
    'resources/win/bin/vcomp140d.dll' = 'bin/vcomp140d.dll'
    'LICENSE' = 'LICENSE'
}
foreach ($model in $models) {
    foreach ($extension in @('bin', 'param')) {
        $files["resources/models/$model.$extension"] = "models/$model.$extension"
    }
}

if ((Test-Path -LiteralPath (Join-Path $target 'REVISAO.txt')) -and
    ((Get-Content -LiteralPath (Join-Path $target 'REVISAO.txt') -First 1) -eq $revision) -and
    !($files.Values | Where-Object { !(Test-Path -LiteralPath (Join-Path $target $_) -PathType Leaf) })) {
    Write-Host 'Upscayl portátil já preparado.'
    return
}

$oldProgress = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'
try {
    Write-Host 'Baixando o motor e os modelos oficiais do Upscayl 2.15.0…'
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($file in $files.Keys) {
            $entry = $zip.GetEntry("upscayl-$revision/$file")
            if (!$entry -or $entry.Length -lt 1) { throw "Arquivo ausente no pacote oficial: $file" }
        }
        foreach ($file in $files.Keys) {
            $entry = $zip.GetEntry("upscayl-$revision/$file")
            $path = Join-Path $target $files[$file]
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    } finally { $zip.Dispose() }

    @($revision, 'https://github.com/upscayl/upscayl/releases/tag/v2.15.0',
      'https://github.com/upscayl/upscayl-ncnn', $url) |
        Set-Content -LiteralPath (Join-Path $target 'REVISAO.txt') -Encoding UTF8
    Write-Host "Upscayl portátil preparado em $target"
} finally {
    $ProgressPreference = $oldProgress
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
}
