param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
$savedPassword = $env:WANQIX_ROOT_PASSWORD
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('Wanqix.CLI.' + [guid]::NewGuid().ToString('N'))
function Invoke-Checked {
    param([string[]]$DotnetArguments)
    & dotnet @DotnetArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
try {
    Invoke-Checked @('build', 'Wanqix.slnx', '-c', 'Debug', '--nologo')
    Invoke-Checked @('build', 'Wanqix.slnx', '-c', 'Release', '--nologo')
    Invoke-Checked @('run', '--project', 'tests/Wanqix.Tests', '-c', 'Release', '--no-build')
    $env:WANQIX_ROOT_PASSWORD = 'CLI-fixture-' + [guid]::NewGuid().ToString('N')
    Invoke-Checked @('run', '--project', 'src/Wanqix.Console', '-c', 'Release', '--no-build', '--', '--data', $fixtureRoot, '--admin', '--deterministic', '--script', 'examples/scripts/demo.ksl')
    Invoke-Checked @('run', '--project', 'src/Wanqix.Console', '-c', 'Release', '--no-build', '--', '--data', $fixtureRoot, '--admin', '--deterministic', '--command', 'fs.read /temp/wanqix-note.txt; sys.info')
    & dotnet run --project src/Wanqix.Console -c Release --no-build -- --data $fixtureRoot --deterministic --command 'svc.stop logger'
    if ($LASTEXITCODE -ne 1) { throw 'Guest denial must produce exit code 1.' }
    if ($Publish) {
        Invoke-Checked @('publish', 'src/Wanqix.Console', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', 'publish/win-x64')
    }
    Write-Host "PASS: build, subsystems, CLI, persistence and guest denial. Fixture retained at $fixtureRoot"
}
finally {
    $env:WANQIX_ROOT_PASSWORD = $savedPassword
    Pop-Location
}
