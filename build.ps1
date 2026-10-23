param([switch]$SkipBedrock)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskRoot = $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'artifacts\app'
$taskCache = Join-Path $taskRoot '.cache'
New-Item -ItemType Directory -Path $taskOutput,$taskCache -Force | Out-Null
& (Join-Path $taskRoot 'build-seed-map.ps1')

function Get-VerifiedArchive([string]$Url,[string]$Name,[string]$Hash) {
    $taskFile = Join-Path $taskCache $Name
    if ((Test-Path -LiteralPath $taskFile) -and (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -eq $Hash) { return $taskFile }
    $taskPartial = $taskFile + '.' + [guid]::NewGuid().ToString('N') + '.part'
    try {
        $taskCurl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if ($taskCurl) {
            & $taskCurl.Source -fL --retry 2 --retry-all-errors --connect-timeout 20 --max-time 300 -o $taskPartial $Url
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $Name" }
        } else {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $taskPartial
        }
        if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch: $Name" }
        Move-Item -LiteralPath $taskPartial -Destination $taskFile -Force
    } finally {
        if (Test-Path -LiteralPath $taskPartial) { Remove-Item -LiteralPath $taskPartial }
    }
    return $taskFile
}

dotnet restore (Join-Path $taskRoot 'src\BlockDock.csproj') --configfile (Join-Path $taskRoot 'src\NuGet.Config') -p:MSBuildEnableWorkloadResolver=false
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
dotnet publish (Join-Path $taskRoot 'src\BlockDock.csproj') -c Release --no-restore -o $taskOutput -p:MSBuildEnableWorkloadResolver=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($taskName in @('LICENSE.txt','NOTICE.md','README.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot $taskName) -Destination $taskOutput -Force }
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination $taskOutput -Recurse -Force

if (-not $SkipBedrock) {
    $taskBinary = Get-VerifiedArchive 'https://github.com/BedrockLauncher/BedrockLauncher/releases/download/2026.10.19.270/BedrockLauncher-2026.10.19.270.zip' 'BedrockLauncher-2026.10.19.270.zip' 'D23A5E778F1250AFD258DA4E03D1617FE6E7B435133101F9BFD522E06B666097'
    $taskSource = Get-VerifiedArchive 'https://codeload.github.com/BedrockLauncher/BedrockLauncher/zip/09282ecb8f47262af519df1ce1e50dba970e016c' 'BedrockLauncher-source.zip' '504217803F405F168BE7FF26538A0C1AFAB3D61D9B4671C1A1075044BDE0953D'
    $taskLocalization = Get-VerifiedArchive 'https://codeload.github.com/BedrockLauncher/BedrockLauncher.Localization/zip/0f909b21c1229728f7c858a3bb475743b77a2e8c' 'BedrockLauncher-Localization-source.zip' '7129C6771233B0BD8B5EADF865D5D9280D2B016270EC1BAAC00CC733B4265E2A'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskEngine = Join-Path $taskOutput 'engines\BedrockLauncher'
    New-Item -ItemType Directory -Path $taskEngine -Force | Out-Null
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskBinary)
    try {
        foreach ($taskName in @('BedrockLauncher.UpdateProcessor.dll','JemExtensions.dll','Newtonsoft.Json.dll','Semver.dll','System.Net.Http.WinHttpHandler.dll')) {
            $taskEntry = $taskZip.GetEntry($taskName)
            if ($null -eq $taskEntry -or $taskEntry.Length -le 0 -or $taskEntry.Length -gt 25MB) { throw "Invalid component: $taskName" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry,(Join-Path $taskEngine $taskName),$true)
        }
    } finally { $taskZip.Dispose() }
    Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskOutput 'licenses\BedrockLauncher-source.zip') -Force
    Copy-Item -LiteralPath $taskLocalization -Destination (Join-Path $taskOutput 'licenses\BedrockLauncher-Localization-source.zip') -Force
}
Write-Output "Build complete: $taskOutput\BlockDock.exe"
