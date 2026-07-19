param(
    [string]$ApiBaseUrl = "http://localhost:7000",
    [int]$TestClubId = 6010
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

function Wait-ForHealth([string]$url) {
    for ($attempt = 1; $attempt -le 15; $attempt++) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing $url -TimeoutSec 3).StatusCode -eq 200) {
                return
            }
        }
        catch {
            if ($attempt -eq 15) {
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

function Assert-Equal($actual, $expected, [string]$message) {
    if ($actual -ne $expected) {
        throw "$message Expected '$expected', received '$actual'."
    }
}

function Assert-Forbidden(
    [string]$uri,
    [string]$method,
    [hashtable]$headers,
    $body,
    [string]$message
) {
    try {
        Invoke-JsonApi -uri $uri -method $method -headers $headers -body $body | Out-Null
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403) {
            return
        }
        throw
    }
    throw $message
}

docker compose restart auth-service | Out-Null
Wait-ForHealth "http://localhost:5101/health"

$sqlContainer = docker inspect clubreport-sqlserver | ConvertFrom-Json
$sqlPassword = Get-ContainerEnvironment $sqlContainer "MSSQL_SA_PASSWORD"

function Invoke-Sql([string]$database, [string]$query) {
    & docker exec clubreport-sqlserver /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U sa -P $sqlPassword -C -d $database -h -1 -W -Q $query
    if ($LASTEXITCODE -ne 0) {
        throw "SQL command failed in $database."
    }
}

$originalCategory = ((Invoke-Sql "ClubReportHub_Club" `
    "SET NOCOUNT ON; SELECT Category FROM Clubs WHERE Id=$TestClubId;") -join "").Trim()
if (-not $originalCategory) {
    throw "Test club $TestClubId was not found."
}

Invoke-Sql "ClubReportHub_Club" @"
SET NOCOUNT ON;
UPDATE Clubs SET Category='TECHNOLOGY' WHERE Id=$TestClubId;
IF NOT EXISTS (
    SELECT 1 FROM ClubManagerAssignments
    WHERE ClubId=$TestClubId AND ManagerUserId=2 AND IsActive=1
)
INSERT INTO ClubManagerAssignments
    (ClubId, ManagerUserId, ManagerName, AssignedAtUtc, IsActive)
VALUES
    ($TestClubId, 2, 'E2E Manager', SYSDATETIMEOFFSET(), 1);
"@ | Out-Null

$applicationId = $null
$createdClubId = $null
$membershipId = $null
$testCode = "E2E" + [DateTime]::UtcNow.ToString("HHmmss")
$testName = "E2E Technology Club " + [DateTime]::UtcNow.ToString("HHmmss")

try {
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
    $admin = Login "admin@club.local" `
        (Get-ContainerEnvironment $authContainer "SeedPasswords__Admin")

    $studentHeaders = @{ Authorization = "Bearer $($student.accessToken)" }
    $managerHeaders = @{ Authorization = "Bearer $($manager.accessToken)" }
    $adminHeaders = @{ Authorization = "Bearer $($admin.accessToken)" }
    Write-Output "PASS login: CLUB_MEMBER / CLUB_MANAGER / ADMIN"

    Assert-Forbidden "$ApiBaseUrl/api/clubs/applications" "Get" `
        $studentHeaders $null `
        "CLUB_MEMBER unexpectedly accessed all creation applications."
    Write-Output "PASS RBAC: CLUB_MEMBER cannot review all club applications"

    $joinPayload = @{
        fullName              = "E2E Student"
        dateOfBirth           = "2003-04-15"
        gender                = "OTHER"
        email                 = "student@club.local"
        phoneNumber           = "0912345678"
        address               = "Hanoi"
        hobbies               = "Robotics and open source"
        skills                = "C#, React"
        reason                = "I want to contribute to technology projects."
        expectations          = "Learn teamwork"
        contributions         = "Build web applications"
        additionalInfo        = @{
            programmingLanguages = "C#, JavaScript"
            projects             = "ClubReportHub"
        }
        acceptedClubRules     = $true
        committedToParticipate = $true
        message               = "E2E structured membership form"
    }

    Assert-Forbidden "$ApiBaseUrl/api/clubs/$TestClubId/join" "Post" `
        $adminHeaders $joinPayload `
        "ADMIN unexpectedly submitted a membership application."
    Write-Output "PASS RBAC: ADMIN cannot use the member join function"

    $membership = Invoke-JsonApi "$ApiBaseUrl/api/clubs/$TestClubId/join" `
        "Post" $studentHeaders $joinPayload
    $membershipId = [int]$membership.id
    Assert-Equal $membership.status "Pending" "Membership status is incorrect."
    Assert-Equal $membership.clubCategory "TECHNOLOGY" "Club category is incorrect."
    Assert-Equal $membership.additionalInfo.programmingLanguages `
        "C#, JavaScript" "Dynamic membership fields were not preserved."
    Assert-Equal $membership.acceptedClubRules $true "Member commitment was not preserved."
    Write-Output "PASS member submitted TECHNOLOGY join form (membership $membershipId)"

    $managerView = Invoke-JsonApi "$ApiBaseUrl/api/clubs/$TestClubId/memberships" `
        "Get" $managerHeaders
    $pending = $managerView | Where-Object { $_.id -eq $membershipId }
    if (-not $pending) {
        throw "Club owner could not see the pending membership."
    }
    Assert-Equal $pending.reason $joinPayload.reason "Join reason is incorrect."
    Assert-Equal $pending.additionalInfo.projects "ClubReportHub" `
        "Club owner could not see dynamic membership details."

    $approvedMembership = Invoke-JsonApi `
        "$ApiBaseUrl/api/clubs/memberships/$membershipId/approve" `
        "Post" $managerHeaders @{ note = "Approved by E2E club owner" }
    Assert-Equal $approvedMembership.status "Approved" "Membership approval failed."
    Assert-Equal $approvedMembership.reviewNote "Approved by E2E club owner" `
        "Membership review note is incorrect."
    Write-Output "PASS club owner reviewed and approved membership"

    $applicationPayload = @{
        code                      = $testCode
        name                      = $testName
        category                  = "TECHNOLOGY"
        purpose                   = "Create a student technology community."
        description               = "A club for practical technology projects."
        logoUrl                   = "https://example.com/e2e-logo.png"
        founderFullName           = "E2E Student"
        founderRole               = "Club President"
        founderEmail              = "student@club.local"
        founderPhone              = "0912345678"
        founderOrganization       = "SE1801 / Software Engineering"
        foundingMemberCount       = 2
        foundingMembers           = @(
            @{
                fullName     = "E2E Student"
                organization = "SE1801"
                email        = "student@club.local"
            },
            @{
                fullName     = "Founding Member"
                organization = "AI1801"
                email        = "founder2@example.com"
            }
        )
        foundingMembersCommitted  = $true
        mainActivities            = "Weekly coding lab, mentoring and technology talks."
        activityFrequency         = "Weekly"
        expectedLocation          = "Innovation Lab"
        expectedSchedule          = "Saturday afternoon"
        majorEvents               = "Annual student technology showcase"
        venueSupport              = "SUPPORT_NEEDED"
        fundingSupport            = "COMBINED"
        equipmentNeeds            = "Projector and development boards"
        advisorNeeded             = $true
        committedToRules          = $true
        committedToResponsibility = $true
        committedToReporting      = $true
    }

    $application = Invoke-JsonApi "$ApiBaseUrl/api/clubs/applications" `
        "Post" $studentHeaders $applicationPayload
    $applicationId = [int]$application.id
    Assert-Equal $application.status "Submitted" "Creation application status is incorrect."
    Assert-Equal $application.category "TECHNOLOGY" "Creation category is incorrect."
    Assert-Equal $application.foundingMembers.Count 2 "Founding member list is incorrect."
    Write-Output "PASS member submitted full club creation form (application $applicationId)"

    $myApplications = Invoke-JsonApi "$ApiBaseUrl/api/clubs/applications/me" `
        "Get" $studentHeaders
    if (-not ($myApplications | Where-Object { $_.id -eq $applicationId })) {
        throw "Member cannot see their own club application."
    }
    $allApplications = Invoke-JsonApi "$ApiBaseUrl/api/clubs/applications" `
        "Get" $adminHeaders
    if (-not ($allApplications | Where-Object { $_.id -eq $applicationId })) {
        throw "Admin cannot see the submitted club application."
    }
    Write-Output "PASS visibility: member sees own form, admin sees review queue"

    $revision = Invoke-JsonApi `
        "$ApiBaseUrl/api/clubs/applications/$applicationId/request-revision" `
        "Post" $adminHeaders @{
            note              = "Please clarify the major event plan."
            conditions        = "Add a measurable outcome."
            reviewerSignature = "Admin E2E"
        }
    Assert-Equal $revision.status "NeedsRevision" "Revision status is incorrect."
    Assert-Equal $revision.reviewConditions "Add a measurable outcome." `
        "Revision conditions are incorrect."
    Write-Output "PASS admin requested revision with note and conditions"

    $applicationPayload.majorEvents = `
        "Annual showcase for 200 students with at least 20 project demos."
    $resubmitted = Invoke-JsonApi `
        "$ApiBaseUrl/api/clubs/applications/$applicationId" `
        "Put" $studentHeaders $applicationPayload
    Assert-Equal $resubmitted.status "Submitted" "Resubmission status is incorrect."
    if ($resubmitted.reviewNote) {
        throw "Review note should be cleared after resubmission."
    }
    Write-Output "PASS member edited and resubmitted requested revision"

    $approvedApplication = Invoke-JsonApi `
        "$ApiBaseUrl/api/clubs/applications/$applicationId/approve" `
        "Post" $adminHeaders @{
            note              = "Approved after revision."
            conditions        = "Submit quarterly reports."
            reviewerSignature = "Admin E2E"
        }
    $createdClubId = [int]$approvedApplication.createdClubId
    Assert-Equal $approvedApplication.status "Approved" "Creation approval failed."
    Assert-Equal $approvedApplication.reviewConditions "Submit quarterly reports." `
        "Approval conditions are incorrect."
    if ($createdClubId -le 0) {
        throw "Approval did not create a club."
    }

    $createdClub = Invoke-JsonApi "$ApiBaseUrl/api/clubs/$createdClubId" `
        "Get" $studentHeaders
    Assert-Equal $createdClub.category "TECHNOLOGY" "Created club category is incorrect."
    if (-not ($createdClub.managers | Where-Object { $_.managerUserId -eq 1004 })) {
        throw "Founder did not become the owner of the approved club."
    }
    Write-Output "PASS admin approved application and founder became owner (club $createdClubId)"
}
finally {
    if ($applicationId) {
        Invoke-Sql "ClubReportHub_Club" `
            "SET NOCOUNT ON; DELETE FROM ClubCreationApplications WHERE Id=$applicationId;" |
            Out-Null
    }
    if ($createdClubId) {
        Invoke-Sql "ClubReportHub_Club" `
            "SET NOCOUNT ON; DELETE FROM Clubs WHERE Id=$createdClubId;" |
            Out-Null
    }

    Invoke-Sql "ClubReportHub_Club" @"
SET NOCOUNT ON;
DELETE FROM ClubMemberships WHERE ClubId=$TestClubId AND UserId=1004;
DELETE FROM ClubManagerAssignments
WHERE ClubId=$TestClubId AND ManagerUserId=2 AND ManagerName='E2E Manager';
UPDATE Clubs SET Category='$originalCategory' WHERE Id=$TestClubId;
"@ | Out-Null

    if ($testCode) {
        Invoke-Sql "ClubReportHub_Notification" `
            "SET NOCOUNT ON; DELETE FROM Notifications WHERE Message LIKE '%$testCode%';" |
            Out-Null
    }
    Write-Output "CLEANUP completed: temporary E2E records were removed"
}
