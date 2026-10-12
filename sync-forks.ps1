param(
    [Parameter(Mandatory)]
    [string]$Org,
    [string]$Log = "$env:USERPROFILE\sync-forks.log"
)

function Write-Log([string]$Message) {
    $line = "$(Get-Date -Format 's') $Message"
    Write-Host $line
    $line | Out-File $Log -Append -Encoding utf8
}

Write-Log "Fetching fork list for $Org"
$repos = @(gh repo list $Org --fork --limit 200 --json nameWithOwner,defaultBranchRef | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) {
    Write-Log "FAILED: cannot list repos for $Org"
    exit 1
}

$total = $repos.Count
Write-Log "Found $total fork(s)"

$failed = @()
$i = 0
foreach ($r in $repos) {
    $i++
    $name = $r.nameWithOwner
    $branch = $r.defaultBranchRef.name
    Write-Progress -Activity "Syncing forks in $Org" -Status "[$i/$total] $name" -PercentComplete ($i / $total * 100)
    Write-Log "[$i/$total] Syncing $name ($branch)"
    gh repo sync $name --branch $branch 2>&1 | Out-File $Log -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        Write-Log "[$i/$total] FAILED: $name"
        $failed += $name
    } else {
        Write-Log "[$i/$total] OK: $name"
    }
}
Write-Progress -Activity "Syncing forks in $Org" -Completed

Write-Log "Done: $($total - $failed.Count) succeeded, $($failed.Count) failed"
if ($failed.Count -gt 0) {
    Write-Log "Failed repos: $($failed -join ', ')"
    exit 1
}
