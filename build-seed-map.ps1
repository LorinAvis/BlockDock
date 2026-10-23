param([string]$Output = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskCache = Join-Path $taskRoot '.cache'
$taskCompiler = Join-Path $taskCache 'zig-windows-x86_64-0.13.0\zig.exe'
if (-not $Output) { $Output = Join-Path $taskRoot 'src\SeedMapNative\bin\BlockDock.SeedMap.dll' }
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
    $taskZip = Join-Path $taskCache 'zig-windows-x86_64-0.13.0.zip'
    $taskHash = 'd859994725ef9402381e557c60bb57497215682e355204d754ee3df75ee3c158'
    if (-not (Test-Path -LiteralPath $taskZip) -or (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash -ne $taskHash) {
        & curl.exe -fLsS --retry 2 --connect-timeout 20 --max-time 300 -o ($taskZip + '.part') 'https://ziglang.org/download/0.13.0/zig-windows-x86_64-0.13.0.zip'
        if ($LASTEXITCODE -ne 0) { throw 'Seed map compiler download failed.' }
        if ((Get-FileHash -LiteralPath ($taskZip + '.part') -Algorithm SHA256).Hash -ne $taskHash) { throw 'Seed map compiler checksum mismatch.' }
        Move-Item -LiteralPath ($taskZip + '.part') -Destination $taskZip -Force
    }
    Expand-Archive -LiteralPath $taskZip -DestinationPath $taskCache -Force
}
$taskSource = Join-Path $taskRoot 'src\SeedMapNative'
New-Item -ItemType Directory -Path (Split-Path $Output -Parent) -Force | Out-Null
$taskArgs = @('cc','-shared','-O2','-fwrapv','-fno-strict-aliasing','-target','x86_64-windows-gnu','-o',$Output,(Join-Path $taskSource 'blockdock_seed.c'))
foreach ($taskName in @('generator','layers','biomenoise','biomes','noise','finders','util','quadbase')) { $taskArgs += Join-Path $taskSource ('cubiomes\' + $taskName + '.c') }
& $taskCompiler @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Offline seed map build failed.' }
Write-Output "Offline seed map built: $Output"
