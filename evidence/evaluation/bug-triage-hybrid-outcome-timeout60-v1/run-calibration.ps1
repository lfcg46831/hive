[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$corpus = Join-Path $repoRoot 'config\organizations\acme-delivery\examples\evaluation\bug-triage-corpus.v1.json'
$rubric = Join-Path $repoRoot 'config\organizations\acme-delivery\examples\evaluation\bug-triage-rubric.v1.json'
$runIds = @(
    'hybrid-outcome-timeout60-calibration-001',
    'hybrid-outcome-timeout60-calibration-002',
    'hybrid-outcome-timeout60-calibration-003'
)

foreach ($runId in $runIds) {
    $output = Join-Path $PSScriptRoot "$runId.json"
    if (Test-Path -LiteralPath $output) {
        throw "Calibration output already exists: $output"
    }
}

$env:ConnectionStrings__PostgreSql = 'Host=localhost;Port=15432;Database=hive;Username=hive;Password=hive'

try {
    Set-Location -LiteralPath $repoRoot
    Write-Output "CALIBRATION_BLOCK_START $((Get-Date).ToUniversalTime().ToString('o'))"

    foreach ($runId in $runIds) {
        $output = Join-Path $PSScriptRoot "$runId.json"
        Write-Output "RUN_START $runId $((Get-Date).ToUniversalTime().ToString('o'))"
        dotnet run --project src/Hive.DemoClient --no-restore -- evaluate `
            --run-id $runId `
            --base-url http://localhost:8080 `
            --corpus $corpus `
            --rubric $rubric `
            --output $output `
            --timeout-seconds 120 `
            --poll-milliseconds 1000
        $exitCode = $LASTEXITCODE
        Write-Output "RUN_END $runId exit=$exitCode $((Get-Date).ToUniversalTime().ToString('o'))"
    }

    Write-Output "CALIBRATION_BLOCK_END $((Get-Date).ToUniversalTime().ToString('o'))"
}
finally {
    Remove-Item Env:ConnectionStrings__PostgreSql -ErrorAction SilentlyContinue
}
