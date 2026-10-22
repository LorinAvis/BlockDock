param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRoot 'src\BlockDock.csproj') -Raw
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
$taskCommit = git -C $taskRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Package from a Git checkout to include exact corresponding source.' }
if (@(git -C $taskRoot status --porcelain).Count) { throw 'Commit source changes before packaging.' }
$taskReleases = Join-Path $taskRoot 'artifacts\releases'
$taskPackageRoot = Join-Path $taskReleases ("BlockDock-$taskVersion-$Runtime")
$taskApp = Join-Path $taskPackageRoot 'BlockDock'
$taskArchive = Join-Path $taskReleases ("BlockDock-$taskVersion-Windows-x64.zip")
if ((Test-Path -LiteralPath $taskPackageRoot) -or (Test-Path -LiteralPath $taskArchive)) { throw 'Release output exists; move it aside before repackaging.' }

& (Join-Path $taskRoot 'build.ps1')
New-Item -ItemType Directory -Path $taskApp -Force | Out-Null
$taskNugetConfig = Join-Path $taskRoot 'src\NuGet.Config'
dotnet publish (Join-Path $taskRoot 'src\BlockDock.csproj') -c Release -r $Runtime --self-contained true -o $taskApp -p:MSBuildEnableWorkloadResolver=false "-p:RestoreConfigFile=$taskNugetConfig"
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$taskBuilt = Join-Path $taskRoot 'artifacts\app'
foreach ($taskName in @('LICENSE.txt','NOTICE.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot $taskName) -Destination $taskApp }
foreach ($taskName in @('licenses','engines')) { Copy-Item -LiteralPath (Join-Path $taskBuilt $taskName) -Destination $taskApp -Recurse }
$taskSource = Join-Path $taskApp 'source'
New-Item -ItemType Directory -Path $taskSource -Force | Out-Null
$taskSourceArchive = Join-Path $taskSource "BlockDock-$taskVersion-source.zip"
git -C $taskRoot archive --format=zip "--output=$taskSourceArchive" $taskCommit
if ($LASTEXITCODE -ne 0) { throw 'Corresponding source archive failed.' }
$taskReadme = @"
BlockDock $taskVersion - Windows x64

Extract the complete folder and run BlockDock.exe.
The .NET runtime is included. Keep the files beside the EXE.
Add your own game download source in Settings. No games or personal accounts are included.

Corresponding source: source/BlockDock-$taskVersion-source.zip
Commit: $taskCommit
Project: https://github.com/LorinAvis/BlockDock
Licenses: LICENSE.txt, NOTICE.md and licenses/
"@
[IO.File]::WriteAllText((Join-Path $taskApp 'README.txt'),$taskReadme,[Text.UTF8Encoding]::new($false))

$taskRuntime = Get-Content -LiteralPath (Join-Path $taskApp 'BlockDock.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($taskRuntime.runtimeOptions.framework -or $taskRuntime.runtimeOptions.frameworks -or -not $taskRuntime.runtimeOptions.includedFrameworks) { throw 'The package requires a preinstalled .NET runtime.' }
foreach ($taskName in @('BlockDock.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll','PresentationFramework.dll','engines\BedrockLauncher\BedrockLauncher.UpdateProcessor.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskApp $taskName))) { throw "Missing release file: $taskName" }
}
$taskFiles = @(Get-ChildItem -LiteralPath $taskApp -File -Recurse)
foreach ($taskFile in $taskFiles) {
    $taskRelative = $taskFile.FullName.Substring($taskApp.Length + 1).Replace('\','/')
    if ($taskRelative -match '(^|/)(data|bin|obj|\.git|\.cache)/|\.log$|\.(appx|msix|msixvc|pfx|key)$|(^|/)(Packet\.dll|easytier-core\.exe|easytier-cli\.exe)$') { throw "Excluded release file: $taskRelative" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::Open($taskArchive,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskFile in $taskFiles) {
        $taskEntryName = $taskFile.FullName.Substring($taskPackageRoot.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,$taskFile.FullName,$taskEntryName,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $taskZip.Dispose() }
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskArchive + '.sha256.txt',$taskHash + '  ' + [IO.Path]::GetFileName($taskArchive) + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Write-Output "Release: $taskArchive"
Write-Output "SHA256: $taskHash"
