Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\..\scripts\Test-LocalStack.ps1'
. $scriptPath -ExpectedNodes 1

$script:failureCount = 0

function Invoke-TestCase {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Test
    )

    try {
        & $Test
        Write-Host "PASS: $Name"
    }
    catch {
        $script:failureCount++
        Write-Host "FAIL: $Name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-ThrowsContaining {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$ExpectedText
    )

    $caughtMessage = $null
    try {
        & $Action
    }
    catch {
        $caughtMessage = $_.Exception.Message
    }

    if ($null -eq $caughtMessage) {
        throw 'Expected the action to throw, but it completed successfully.'
    }

    if ($caughtMessage.IndexOf($ExpectedText, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Expected error containing '$ExpectedText', got '$caughtMessage'."
    }
}

function New-ContainerSnapshot {
    param(
        [Parameter(Mandatory)]
        [string]$Service,

        [string]$State = 'running',

        [string]$Health = 'healthy'
    )

    [pscustomobject]@{
        Service = $Service
        State = $State
        Health = $Health
    }
}

Invoke-TestCase 'accepts the healthy one-node topology' {
    Assert-ExpectedServices -Services @('postgres', 'api') -ExpectedNodes 1
    Assert-HealthyContainers -Containers @(
        (New-ContainerSnapshot -Service 'postgres'),
        (New-ContainerSnapshot -Service 'api')
    ) -ExpectedServices @('postgres', 'api')
}

Invoke-TestCase 'accepts the healthy three-node topology' {
    Assert-ExpectedServices -Services @('postgres', 'api', 'api2', 'api3') -ExpectedNodes 3
    Assert-HealthyContainers -Containers @(
        (New-ContainerSnapshot -Service 'postgres'),
        (New-ContainerSnapshot -Service 'api'),
        (New-ContainerSnapshot -Service 'api2'),
        (New-ContainerSnapshot -Service 'api3')
    ) -ExpectedServices @('postgres', 'api', 'api2', 'api3')
}

Invoke-TestCase 'rejects an unexpected HIVE node' {
    Assert-ThrowsContaining -ExpectedText 'expected [api, postgres], actual [api, api2, postgres]' -Action {
        Assert-ExpectedServices -Services @('postgres', 'api', 'api2') -ExpectedNodes 1
    }
}

Invoke-TestCase 'rejects a missing HIVE node' {
    Assert-ThrowsContaining -ExpectedText 'expected [api, api2, api3, postgres], actual [api, api2, postgres]' -Action {
        Assert-ExpectedServices -Services @('postgres', 'api', 'api2') -ExpectedNodes 3
    }
}

Invoke-TestCase 'rejects a stopped service' {
    Assert-ThrowsContaining -ExpectedText "service 'api2' is not running" -Action {
        Assert-HealthyContainers -Containers @(
            (New-ContainerSnapshot -Service 'postgres'),
            (New-ContainerSnapshot -Service 'api'),
            (New-ContainerSnapshot -Service 'api2' -State 'exited'),
            (New-ContainerSnapshot -Service 'api3')
        ) -ExpectedServices @('postgres', 'api', 'api2', 'api3')
    }
}

Invoke-TestCase 'rejects an unhealthy service' {
    Assert-ThrowsContaining -ExpectedText "service 'postgres' is not healthy" -Action {
        Assert-HealthyContainers -Containers @(
            (New-ContainerSnapshot -Service 'postgres' -Health 'unhealthy'),
            (New-ContainerSnapshot -Service 'api')
        ) -ExpectedServices @('postgres', 'api')
    }
}

Invoke-TestCase 'rejects a missing running container' {
    Assert-ThrowsContaining -ExpectedText "service 'api3' has no container" -Action {
        Assert-HealthyContainers -Containers @(
            (New-ContainerSnapshot -Service 'postgres'),
            (New-ContainerSnapshot -Service 'api'),
            (New-ContainerSnapshot -Service 'api2')
        ) -ExpectedServices @('postgres', 'api', 'api2', 'api3')
    }
}

Invoke-TestCase 'parses Docker Compose JSON arrays' {
    $containers = @(ConvertFrom-ComposePsJson -Json @(
        '[{"Service":"postgres","State":"running","Health":"healthy"},{"Service":"api","State":"running","Health":"healthy"}]'
    ))

    if ($containers.Count -ne 2 -or $containers[1].Service -ne 'api') {
        throw 'Expected two parsed containers ending with the api service.'
    }
}

Invoke-TestCase 'parses line-delimited Docker Compose JSON' {
    $containers = @(ConvertFrom-ComposePsJson -Json @(
        '{"Service":"postgres","State":"running","Health":"healthy"}',
        '{"Service":"api","State":"running","Health":"healthy"}'
    ))

    if ($containers.Count -ne 2 -or $containers[0].Service -ne 'postgres') {
        throw 'Expected two parsed containers beginning with the postgres service.'
    }
}

Invoke-TestCase 'keeps successful Docker diagnostics out of standard output' {
    function docker {
        $global:LASTEXITCODE = 0
        Write-Error 'diagnostic warning' -ErrorAction Continue
        return @('postgres', 'api')
    }

    $output = @(Invoke-DockerCompose -Arguments @('config', '--services') 2>$null)
    if ($output.Count -ne 2 -or $output[0] -ne 'postgres' -or $output[1] -ne 'api') {
        throw "Expected only service names, got [$($output -join ', ')]."
    }
}

Invoke-TestCase 'orchestrates a successful one-node smoke check' {
    function docker {
        $command = $args -join ' '
        $global:LASTEXITCODE = 0

        if ($command -like '* config --services') {
            return @('postgres', 'api')
        }

        if ($command -like '* ps --format json') {
            return '[{"Service":"postgres","State":"running","Health":"healthy"},{"Service":"api","State":"running","Health":"healthy"}]'
        }

        if ($command -like '* exec -T postgres *') {
            return '127.0.0.1:5432 - accepting connections'
        }

        $global:LASTEXITCODE = 1
        return "Unexpected docker invocation: $command"
    }

    function Invoke-WebRequest {
        [pscustomobject]@{ StatusCode = 200 }
    }

    Invoke-LocalStackSmokeCheck -NodeCount 1
}

if ($script:failureCount -gt 0) {
    Write-Host "$script:failureCount smoke validation test(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All smoke validation tests passed.' -ForegroundColor Green
exit 0
