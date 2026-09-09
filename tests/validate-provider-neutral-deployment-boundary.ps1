$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$prohibitedPaths = @(
    '.github/workflows/deploy-appservice.yml',
    '.github/workflows/azure-deploy.yml',
    'ops/azure/bootstrap-github-oidc-deployment.ps1'
)

foreach ($relative in $prohibitedPaths) {
    $path = Join-Path $repoRoot $relative
    if (Test-Path $path) {
        throw "Retired Azure production path is present: $relative"
    }
}

$workflowRoot = Join-Path $repoRoot '.github/workflows'
$workflowFiles = if (Test-Path $workflowRoot) { @(Get-ChildItem $workflowRoot -File -Include *.yml,*.yaml) } else { @() }
$prohibitedMarkers = @(
    'azure/login@',
    'azure/webapps-deploy@',
    'AZURE_CLIENT_ID',
    'AZURE_TENANT_ID',
    'AZURE_SUBSCRIPTION_ID',
    'AZURE_WEBAPP_PUBLISH_PROFILE'
)

foreach ($workflow in $workflowFiles) {
    $content = Get-Content $workflow.FullName -Raw
    foreach ($marker in $prohibitedMarkers) {
        if ($content -match [regex]::Escape($marker)) {
            throw "Retired Azure production dependency '$marker' found in workflow $($workflow.Name)"
        }
    }
}

$boundaryDoc = Join-Path $repoRoot 'governance/PROVIDER-NEUTRAL-DEPLOYMENT-BOUNDARY.md'
if (-not (Test-Path $boundaryDoc)) {
    throw 'Missing provider-neutral deployment boundary documentation.'
}

$boundary = Get-Content $boundaryDoc -Raw
foreach ($required in @('PROVIDER_NEUTRAL', 'JPV_OS', 'NO_ADMITTED_EXECUTION_CAPACITY', 'fail closed')) {
    if ($boundary -notmatch [regex]::Escape($required)) {
        throw "Provider-neutral boundary is missing required marker: $required"
    }
}

$retiredOperationalDocs = @(
    'docs/AZURE-APP-SERVICE-DEPLOYMENT.md',
    'docs/TEAMS-NOTIFY-SETUP.md'
)
foreach ($relative in $retiredOperationalDocs) {
    $path = Join-Path $repoRoot $relative
    if (-not (Test-Path $path)) {
        throw "Expected retirement notice is missing: $relative"
    }
    $content = Get-Content $path -Raw
    foreach ($required in @('Status: RETIRED', 'PROVIDER-NEUTRAL-DEPLOYMENT-BOUNDARY.md', 'non-operational')) {
        if ($content -notmatch [regex]::Escape($required)) {
            throw "Retired deployment documentation '$relative' is missing marker: $required"
        }
    }
    foreach ($prohibited in @('Canonical production workflow:', 'Using GitHub Actions (Recommended)', 'fully automated to notify')) {
        if ($content -match [regex]::Escape($prohibited)) {
            throw "Retired deployment documentation '$relative' still presents an obsolete operational instruction: $prohibited"
        }
    }
}

Write-Host 'Provider-neutral deployment boundary validated.'
