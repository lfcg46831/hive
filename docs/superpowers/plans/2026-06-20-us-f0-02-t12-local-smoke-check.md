# US-F0-02-T12 Local Smoke Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a non-mutating PowerShell smoke check that validates an already-running one-node or three-node HIVE Compose environment.

**Architecture:** A single entry script selects the canonical Compose file set, delegates parsed service/container data to pure assertion functions, and performs PostgreSQL and HTTP probes. A dependency-free PowerShell test runner dot-sources the script and tests the pure assertions without Docker; the operational guide documents the two supported invocations.

**Tech Stack:** PowerShell, Docker Compose v2, PostgreSQL `pg_isready`, HIVE HTTP readiness endpoint, .NET 8 regression tests.

---

## File Structure

- Create `scripts/Test-LocalStack.ps1`: command entry point, Compose invocation, JSON normalization, pure topology/state assertions, PostgreSQL probe, and API readiness probe.
- Create `tests/Smoke/LocalStackSmoke.Tests.ps1`: dependency-free tests for accepted and rejected topology/container snapshots.
- Modify `docs/configuration.md`: operational commands and prerequisites only.
- Keep `docs/bible.html` unchanged: it already owns the T12 requirement and no architecture decision changes.

No Git commit is created by this plan. Per the repository instructions, completion supplies a short English commit-message summary for the user.

### Task 1: Specify topology and container-state behavior

**Files:**
- Create: `tests/Smoke/LocalStackSmoke.Tests.ps1`
- Test target: `scripts/Test-LocalStack.ps1`

- [ ] **Step 1: Write the failing dependency-free test runner**

Create `tests/Smoke/LocalStackSmoke.Tests.ps1` with the following content:

```powershell
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

if ($script:failureCount -gt 0) {
    Write-Host "$script:failureCount smoke validation test(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All smoke validation tests passed.' -ForegroundColor Green
exit 0
```

- [ ] **Step 2: Run the tests and verify the expected red state**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Smoke/LocalStackSmoke.Tests.ps1
```

Expected: exit code `1`; dot-sourcing fails because `scripts/Test-LocalStack.ps1` does not exist.

### Task 2: Implement pure smoke validation and make focused tests green

**Files:**
- Create: `scripts/Test-LocalStack.ps1`
- Test: `tests/Smoke/LocalStackSmoke.Tests.ps1`

- [ ] **Step 1: Add parameters, expected-service resolution, and pure assertions**

Create `scripts/Test-LocalStack.ps1` with:

```powershell
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

```

- [ ] **Step 2: Run the focused tests and verify green**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Smoke/LocalStackSmoke.Tests.ps1
```

Expected: seven `PASS` lines followed by `All smoke validation tests passed.` and exit code `0`.

### Task 3: Implement Compose and probe orchestration

**Files:**
- Modify: `scripts/Test-LocalStack.ps1`
- Test: `tests/Smoke/LocalStackSmoke.Tests.ps1`

- [ ] **Step 1: Add failing JSON, stream-separation, and orchestration tests**

Before the final failure-count block in `tests/Smoke/LocalStackSmoke.Tests.ps1`, add:

```powershell
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
```

Run the focused test command and expect these four cases to fail because the orchestration functions do not exist.

- [ ] **Step 2: Add a checked Docker Compose invocation wrapper**

Insert after `Assert-HealthyContainers`:

```powershell
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
```

- [ ] **Step 3: Add Compose JSON normalization**

Insert after `Invoke-DockerCompose`:

```powershell
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
```

- [ ] **Step 4: Add topology argument resolution**

Insert after `ConvertFrom-ComposePsJson`:

```powershell
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
```

- [ ] **Step 5: Add the PostgreSQL and API probes**

Insert after `Get-ComposeFileArguments`:

```powershell
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
```

- [ ] **Step 6: Add the main routine and guarded entry point**

Append:

```powershell
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
```

- [ ] **Step 7: Run focused tests after orchestration changes**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Smoke/LocalStackSmoke.Tests.ps1
```

Expected: all eleven tests pass with exit code `0`; dot-sourcing does not invoke real Docker or HTTP commands.

- [ ] **Step 8: Verify both Compose configurations resolve**

Ensure `.env` exists locally without overwriting an existing file:

```powershell
if (-not (Test-Path .env)) { Copy-Item .env.example .env }
docker compose -f docker-compose.yml config --services
docker compose -f docker-compose.yml -f docker-compose.cluster.yml config --services
```

Expected: the first command lists `postgres` and `api`; the second lists `postgres`, `api`, `api2`, and `api3`.

### Task 4: Document the operational smoke commands

**Files:**
- Modify: `docs/configuration.md`

- [ ] **Step 1: Add the smoke-check section after Local stack lifecycle**

Add:

````markdown
### Local stack smoke check

After the selected topology is running and its containers have become healthy, run the non-mutating smoke check from the repository root. The command validates the resolved node count, Docker health for PostgreSQL and every expected HIVE node, PostgreSQL connectivity through `pg_isready`, and API readiness over HTTP. It does not start, stop, rebuild, or clean the stack.

One-node topology:

```powershell
./scripts/Test-LocalStack.ps1 -ExpectedNodes 1
```

Three-node topology:

```powershell
./scripts/Test-LocalStack.ps1 -ExpectedNodes 3
```

The command exits `0` only when every check passes and exits non-zero with the failed invariant otherwise. Pass the node count matching the Compose file set used to start the environment.
````

- [ ] **Step 2: Check documentation placement and formatting**

Run:

```powershell
rg -n -A 24 '### Local stack smoke check' docs/configuration.md
git diff --check
```

Expected: the operational section appears once under Docker Compose instructions and `git diff --check` reports no errors.

### Task 5: Verify the completed task

**Files:**
- Verify: `scripts/Test-LocalStack.ps1`
- Verify: `tests/Smoke/LocalStackSmoke.Tests.ps1`
- Verify: `docs/configuration.md`
- Verify unchanged contract: `docs/bible.html`

- [ ] **Step 1: Run the focused smoke validation tests**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Smoke/LocalStackSmoke.Tests.ps1
```

Expected: eleven passing cases and exit code `0`.

- [ ] **Step 2: Run the existing .NET regression suite**

Run:

```powershell
dotnet test Hive.sln --no-restore -v minimal
```

Expected: all existing tests pass with exit code `0`.

- [ ] **Step 3: Build the solution**

Run:

```powershell
dotnet build Hive.sln --no-restore -v minimal
```

Expected: build succeeds with zero errors.

- [ ] **Step 4: Run the live smoke check when the matching stack is available**

Inspect the active topology without changing it:

```powershell
docker compose ps --format json
docker compose -f docker-compose.yml -f docker-compose.cluster.yml ps --format json
```

Run only the command matching the active topology:

```powershell
./scripts/Test-LocalStack.ps1 -ExpectedNodes 1
./scripts/Test-LocalStack.ps1 -ExpectedNodes 3
```

Expected: four `PASS` lines and the final topology success message. If no stack is running, retain the focused tests and Compose-resolution evidence and report that live verification was unavailable; do not start a stack because the approved design is non-mutating.

- [ ] **Step 5: Review task scope and final diff**

Run:

```powershell
git diff --check
git status --short
git diff -- scripts/Test-LocalStack.ps1 tests/Smoke/LocalStackSmoke.Tests.ps1 docs/configuration.md docs/superpowers/specs/2026-06-20-us-f0-02-t12-local-smoke-check-design.md docs/superpowers/plans/2026-06-20-us-f0-02-t12-local-smoke-check.md
```

Expected: only the T12 script, tests, operational documentation, approved design, and plan are changed; no credential file is tracked.

- [ ] **Step 6: Prepare the required commit-message summary**

Use this short English message unless the final implementation materially changes:

```text
test(compose): add local stack smoke check for one and three nodes
```
