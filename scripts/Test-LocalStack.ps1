[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(1, 3)]
    [int]$ExpectedNodes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ExpectedServices {
    param(
        [Parameter(Mandatory)]
        [ValidateSet(1, 3)]
        [int]$NodeCount
    )

    if ($NodeCount -eq 1) {
        return @('postgres', 'api')
    }

    return @('postgres', 'api', 'api2', 'api3')
}

function Assert-ExpectedServices {
    param(
        [Parameter(Mandatory)]
        [string[]]$Services,

        [Parameter(Mandatory)]
        [ValidateSet(1, 3)]
        [int]$ExpectedNodes
    )

    $expected = @(Get-ExpectedServices -NodeCount $ExpectedNodes | Sort-Object)
    $actual = @($Services | Sort-Object -Unique)
    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)

    if ($difference.Count -gt 0) {
        throw "Compose service set does not match topology: expected [$($expected -join ', ')], actual [$($actual -join ', ')]."
    }
}

function Assert-HealthyContainers {
    param(
        [Parameter(Mandatory)]
        [object[]]$Containers,

        [Parameter(Mandatory)]
        [string[]]$ExpectedServices
    )

    foreach ($service in $ExpectedServices) {
        $container = @($Containers | Where-Object { $_.Service -eq $service })

        if ($container.Count -eq 0) {
            throw "Compose service '$service' has no container."
        }

        if ($container.Count -gt 1) {
            throw "Compose service '$service' has multiple containers; expected exactly one."
        }

        if ($container[0].State -ne 'running') {
            throw "Compose service '$service' is not running (state: '$($container[0].State)')."
        }

        if ($container[0].Health -ne 'healthy') {
            throw "Compose service '$service' is not healthy (health: '$($container[0].Health)')."
        }
    }
}

function Invoke-DockerCompose {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& docker compose @Arguments)
    if ($LASTEXITCODE -ne 0) {
        $detail = @($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "docker compose failed with exit code $LASTEXITCODE.`n$detail"
    }

    return @($output | ForEach-Object { $_.ToString() })
}

function ConvertFrom-ComposePsJson {
    param(
        [Parameter(Mandatory)]
        [string[]]$Json
    )

    $text = ($Json -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return @()
    }

    try {
        if ($text.StartsWith('[')) {
            foreach ($item in ($text | ConvertFrom-Json)) {
                $item
            }
            return
        }

        return @($Json | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            $_ | ConvertFrom-Json
        })
    }
    catch {
        throw "docker compose ps returned invalid JSON: $($_.Exception.Message)"
    }
}

function Get-ComposeFileArguments {
    param(
        [Parameter(Mandatory)]
        [ValidateSet(1, 3)]
        [int]$NodeCount
    )

    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $arguments = @('-f', (Join-Path $repositoryRoot 'docker-compose.yml'))

    if ($NodeCount -eq 3) {
        $arguments += @('-f', (Join-Path $repositoryRoot 'docker-compose.cluster.yml'))
    }

    return $arguments
}

function Test-PostgreSqlAccess {
    param(
        [Parameter(Mandatory)]
        [string[]]$ComposeFileArguments
    )

    $probeArguments = $ComposeFileArguments + @(
        'exec', '-T', 'postgres',
        'sh', '-ec',
        'pg_isready -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
    )
    [void](Invoke-DockerCompose -Arguments $probeArguments)
}

function Test-ApiReadiness {
    $response = Invoke-WebRequest `
        -Uri 'http://localhost:8080/health/ready' `
        -Method Get `
        -UseBasicParsing `
        -TimeoutSec 10

    if ($response.StatusCode -ne 200) {
        throw "API readiness returned HTTP $($response.StatusCode); expected 200."
    }
}

function Invoke-LocalStackSmokeCheck {
    param(
        [Parameter(Mandatory)]
        [ValidateSet(1, 3)]
        [int]$NodeCount
    )

    $composeFiles = @(Get-ComposeFileArguments -NodeCount $NodeCount)
    $expectedServices = @(Get-ExpectedServices -NodeCount $NodeCount)

    $configuredServices = @(Invoke-DockerCompose -Arguments ($composeFiles + @('config', '--services')))
    Assert-ExpectedServices -Services $configuredServices -ExpectedNodes $NodeCount
    Write-Host "PASS: Compose config contains the expected $NodeCount HIVE node(s)."

    $containerJson = @(Invoke-DockerCompose -Arguments ($composeFiles + @('ps', '--format', 'json')))
    $containers = @(ConvertFrom-ComposePsJson -Json $containerJson)
    Assert-HealthyContainers -Containers $containers -ExpectedServices $expectedServices
    Write-Host 'PASS: PostgreSQL and all expected HIVE containers are running and healthy.'

    Test-PostgreSqlAccess -ComposeFileArguments $composeFiles
    Write-Host 'PASS: PostgreSQL accepts connections.'

    Test-ApiReadiness
    Write-Host 'PASS: HIVE API readiness returned HTTP 200.'

    Write-Host "Local HIVE smoke check passed for the $NodeCount-node topology." -ForegroundColor Green
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-LocalStackSmokeCheck -NodeCount $ExpectedNodes
        exit 0
    }
    catch {
        Write-Host "Local HIVE smoke check failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}
