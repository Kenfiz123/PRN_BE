param(
    [string]$ApiBaseUrl = "http://localhost:7000",
    [int]$ExistingClubId = 6010
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

function Wait-ForHealth([string]$url) {
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing $url -TimeoutSec 3).StatusCode -eq 200) {
                return
            }
        }
        catch {
            if ($attempt -eq 20) {
                throw
            }
            Start-Sleep -Seconds 1
        }
    }
}

function Get-ContainerEnvironment($container, [string]$name) {
    $prefix = "$name="
    return ($container[0].Config.Env |
        Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1).Substring($prefix.Length)
}

function Invoke-JsonApi(
    [string]$uri,
    [string]$method = "Get",
    [hashtable]$headers = @{},
    $body = $null
) {
    $arguments = @{
        Uri         = $uri
        Method      = $method
        Headers     = $headers
        ContentType = "application/json"
    }
    if ($null -ne $body) {
        $arguments.Body = $body | ConvertTo-Json -Depth 12 -Compress
    }
    return Invoke-RestMethod @arguments
}

function Assert-Forbidden(
    [string]$uri,
    [string]$method,
    [hashtable]$headers,
    $body,
    [string]$message
) {
    try {
        Invoke-JsonApi $uri $method $headers $body | Out-Null
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403) {
            return
        }
        throw
    }
    throw $message
}

docker compose restart auth-service report-service | Out-Null
Wait-ForHealth "http://localhost:5101/health"
Wait-ForHealth "http://localhost:5103/health"

$sqlContainer = docker inspect clubreport-sqlserver | ConvertFrom-Json
$sqlPassword = Get-ContainerEnvironment $sqlContainer "MSSQL_SA_PASSWORD"

function Invoke-Sql([string]$database, [string]$query) {
    & docker exec clubreport-sqlserver /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U sa -P $sqlPassword -C -d $database -h -1 -W -Q $query
    if ($LASTEXITCODE -ne 0) {
        throw "SQL command failed in $database."
    }
}

$suffix = [DateTime]::UtcNow.ToString("HHmmss")
$reportPeriod = "E2E-$suffix"
$deadlinePeriod = "E2E-DEADLINE-$suffix"
$softDeleteCode = "SD$suffix"
$temporaryClubId = $null
$exportRequestId = $null
$exportFilePath = $null

Invoke-Sql "ClubReportHub_Club" @"
SET NOCOUNT ON;
IF NOT EXISTS (
    SELECT 1 FROM ClubManagerAssignments
    WHERE ClubId=$ExistingClubId AND ManagerUserId=2 AND IsActive=1
)
INSERT INTO ClubManagerAssignments
    (ClubId, ManagerUserId, ManagerName, AssignedAtUtc, IsActive)
VALUES
    ($ExistingClubId, 2, 'Priority E2E Manager', SYSDATETIMEOFFSET(), 1);

IF NOT EXISTS (
    SELECT 1 FROM ClubMemberships
    WHERE ClubId=$ExistingClubId AND UserId=1003
)
INSERT INTO ClubMemberships
    (ClubId, UserId, FullName, Gender, Email, PhoneNumber, Address, Role, Status,
     PersonalInfo, Goals, Reason, Hobbies, Skills, Expectations, Contributions,
     AdditionalInfoJson, AcceptedClubRules, CommittedToParticipate, RequestedAtUtc)
VALUES
    ($ExistingClubId, 1003, 'Priority E2E Treasurer', '', 'treasurer@club.local',
     '', '', 'TREASURER', 'Approved', '', '', '', '', '', '', '', '{}', 1, 1,
     SYSDATETIMEOFFSET());
"@ | Out-Null

Invoke-Sql "ClubReportHub_Report" @"
SET NOCOUNT ON;
INSERT INTO ReportingDeadlines (Period, DueDate, IsActive)
VALUES ('$deadlinePeriod', DATEADD(day, 7, CAST(GETUTCDATE() AS date)), 1);
"@ | Out-Null

$temporaryClubId = ((Invoke-Sql "ClubReportHub_Club" @"
SET NOCOUNT ON;
INSERT INTO Clubs
    (Code, Name, Category, Description, ContactEmail, ContactPhone, IsActive, CreatedAtUtc)
VALUES
    ('$softDeleteCode', 'Soft Delete E2E Club', 'OTHER', 'Temporary soft-delete verification.',
     'e2e@example.com', '0912345678', 1, SYSDATETIMEOFFSET());
SELECT CAST(SCOPE_IDENTITY() AS int);
"@) -join "").Trim()

try {
    docker compose restart report-service | Out-Null
    Wait-ForHealth "http://localhost:5103/health"

    $authContainerId = (docker compose ps -q auth-service).Trim()
    $authContainer = docker inspect $authContainerId | ConvertFrom-Json
    function Login([string]$username, [string]$password) {
        return Invoke-JsonApi "$ApiBaseUrl/api/auth/login" "Post" @{} @{
            username = $username
            password = $password
        }
    }

    $student = Login "student@club.local" `
        (Get-ContainerEnvironment $authContainer "SeedPasswords__Student")
    $manager = Login "manager@club.local" `
        (Get-ContainerEnvironment $authContainer "SeedPasswords__Manager")
    $treasurer = Login "treasurer@club.local" `
        (Get-ContainerEnvironment $authContainer "SeedPasswords__Treasurer")
    $admin = Login "admin@club.local" `
        (Get-ContainerEnvironment $authContainer "SeedPasswords__Admin")

    $studentHeaders = @{ Authorization = "Bearer $($student.accessToken)" }
    $managerHeaders = @{ Authorization = "Bearer $($manager.accessToken)" }
    $treasurerHeaders = @{ Authorization = "Bearer $($treasurer.accessToken)" }
    $adminHeaders = @{ Authorization = "Bearer $($admin.accessToken)" }

    Assert-Forbidden "$ApiBaseUrl/api/reports/aggregate" "Get" `
        $studentHeaders $null "CLUB_MEMBER accessed report aggregation."
    Assert-Forbidden "$ApiBaseUrl/api/kpis/leaderboard" "Get" `
        $managerHeaders $null "CLUB_MANAGER accessed the global KPI leaderboard."
    Assert-Forbidden "$ApiBaseUrl/api/kpis/leaderboard" "Get" `
        $treasurerHeaders $null "TREASURER accessed the global KPI leaderboard."
    Invoke-JsonApi "$ApiBaseUrl/api/reports/aggregate" "Get" $adminHeaders | Out-Null
    Invoke-JsonApi "$ApiBaseUrl/api/kpis/leaderboard" "Get" $adminHeaders | Out-Null
    Write-Output "PASS P0 analytics authorization: only review administrators can access global data"

    $userSearch = Invoke-JsonApi `
        "$ApiBaseUrl/api/users?search=student%40club.local&page=1&pageSize=1" `
        "Get" $adminHeaders
    if ($userSearch.page -ne 1 -or $userSearch.pageSize -ne 1 `
        -or $userSearch.items.Count -ne 1 `
        -or $userSearch.items[0].username -ne "student@club.local") {
        throw "Server-side user search or pagination returned an invalid result."
    }
    Write-Output "PASS P0 user search is server-side and paginated"

    $managerDeadlines = Invoke-JsonApi "$ApiBaseUrl/api/deadlines/me" `
        "Get" $managerHeaders
    if (-not ($managerDeadlines | Where-Object { $_.period -eq $deadlinePeriod })) {
        throw "Club manager could not see their reporting deadline."
    }
    Assert-Forbidden "$ApiBaseUrl/api/deadlines/me" "Get" `
        $studentHeaders $null "A non-manager accessed /api/deadlines/me."
    Write-Output "PASS P1 /api/deadlines/me is available only to assigned club managers"

    $financialPayload = @{
        clubId    = $ExistingClubId
        clubName  = "Ignored client club name"
        period    = $reportPeriod
        reportType = "FINANCIAL"
        tag       = "FINANCE"
        dueDate   = [DateTime]::UtcNow.AddDays(7).ToString("yyyy-MM-dd")
        details   = @(@{
            id               = $null
            activityName     = "Quarterly budget reconciliation"
            activityDate     = [DateTime]::UtcNow.ToString("yyyy-MM-dd")
            description      = "Financial report prepared by the assigned treasurer."
            participantCount = 0
            outcome          = "Reconciled"
        })
    }
    $financialReport = Invoke-JsonApi "$ApiBaseUrl/api/reports" `
        "Post" $treasurerHeaders $financialPayload
    if ($financialReport.tag -ne "FINANCE" -or $financialReport.createdByUserId -ne 1003) {
        throw "Treasurer financial report creation failed."
    }

    $invalidPayload = $financialPayload.Clone()
    $invalidPayload.period = "$reportPeriod-ACTIVITY"
    $invalidPayload.reportType = "ACTIVITY"
    $invalidPayload.tag = "ACTIVITY"
    Assert-Forbidden "$ApiBaseUrl/api/reports" "Post" `
        $treasurerHeaders $invalidPayload `
        "Treasurer created a non-financial report."
    Write-Output "PASS P3 treasurer can create only financial reports for the assigned club"

    $exportBody = @{
        exportType = "PDF"
        scope      = "e2e"
        period     = $reportPeriod
        clubId     = $ExistingClubId
    } | ConvertTo-Json -Compress
    $exportResponse = Invoke-WebRequest -UseBasicParsing `
        "$ApiBaseUrl/api/exports" `
        -Method Post `
        -Headers $adminHeaders `
        -ContentType "application/json" `
        -Body $exportBody
    if ($exportResponse.StatusCode -ne 202) {
        throw "Export creation should return HTTP 202."
    }
    $export = $exportResponse.Content | ConvertFrom-Json
    $exportRequestId = [int]$export.id
    if ($export.status -ne "Pending") {
        throw "New export request should be Pending."
    }

    $completedExport = $null
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        $completedExport = Invoke-JsonApi `
            "$ApiBaseUrl/api/exports/$exportRequestId" "Get" $adminHeaders
        if ($completedExport.status -in @("Completed", "Failed")) {
            break
        }
    }
    if ($completedExport.status -ne "Completed" -or -not $completedExport.file.isAvailable) {
        throw "Background export job did not complete successfully."
    }
    Write-Output "PASS P1 export API returned 202 and Hangfire completed the job asynchronously"

    Invoke-WebRequest -UseBasicParsing `
        "$ApiBaseUrl/api/clubs/$temporaryClubId" `
        -Method Delete `
        -Headers $adminHeaders | Out-Null
    $deletedClub = Invoke-JsonApi "$ApiBaseUrl/api/clubs/$temporaryClubId" `
        "Get" $adminHeaders
    if ($deletedClub.isActive -or -not $deletedClub.id) {
        throw "Club DELETE did not preserve an inactive soft-deleted record."
    }
    $deletedMetadata = ((Invoke-Sql "ClubReportHub_Club" `
        "SET NOCOUNT ON; SELECT COUNT(*) FROM Clubs WHERE Id=$temporaryClubId AND DeletedAtUtc IS NOT NULL AND DeletedByUserId=1;") -join "").Trim()
    if ($deletedMetadata -ne "1") {
        throw "Soft-delete audit metadata was not stored."
    }
    Write-Output "PASS P2 club deletion is soft-delete with audit metadata"
}
finally {
    if ($exportRequestId) {
        $exportFilePath = ((Invoke-Sql "ClubReportHub_Export" `
            "SET NOCOUNT ON; SELECT FilePath FROM ExportFiles WHERE ExportRequestId=$exportRequestId;") -join "").Trim()
        Invoke-Sql "ClubReportHub_Export" `
            "SET NOCOUNT ON; DELETE FROM ExportRequests WHERE Id=$exportRequestId;" |
            Out-Null
        if ($exportFilePath -and $exportFilePath.StartsWith("/app/exports/", [StringComparison]::Ordinal)) {
            $exportContainerId = (docker compose ps -q export-service).Trim()
            & docker exec $exportContainerId rm -f -- $exportFilePath
        }
    }

    Invoke-Sql "ClubReportHub_Report" @"
SET NOCOUNT ON;
DELETE FROM AuditLogs
WHERE ReportId IN (
    SELECT Id FROM Reports WHERE CreatedByUserId=1003 AND Period LIKE 'E2E-%'
);
DELETE FROM Reports WHERE CreatedByUserId=1003 AND Period LIKE 'E2E-%';
DELETE FROM ReportingDeadlines WHERE Period='$deadlinePeriod';
"@ | Out-Null

    Invoke-Sql "ClubReportHub_Club" @"
SET NOCOUNT ON;
DELETE FROM ClubMemberships WHERE ClubId=$ExistingClubId AND UserId=1003;
DELETE FROM ClubManagerAssignments
WHERE ClubId=$ExistingClubId AND ManagerUserId=2 AND ManagerName='Priority E2E Manager';
DELETE FROM Clubs WHERE Id=$temporaryClubId;
"@ | Out-Null

    Write-Output "CLEANUP completed: priority-fix E2E records were removed"
}
