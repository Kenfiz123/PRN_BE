# Club Member and Activity Attendance Management

## Authorization

- `ADMIN` and `STUDENT_AFFAIRS_ADMIN` can manage every club.
- A club manager can manage only clubs with an active `ClubManagerAssignment` for the authenticated user.
- Every route validates the route `clubId`; member and activity identifiers are also constrained to that club to prevent IDOR.
- Ordinary club members cannot access member-management or attendance-management routes.

## Club Service routes

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/clubs/{clubId}/members` | Server-side search, status/position filters, sorting and pagination with participation statistics. |
| `GET` | `/api/clubs/{clubId}/members/{memberId}` | Member profile, participation summary and paged activity history. |
| `DELETE` | `/api/clubs/{clubId}/members/{memberId}` | Soft-delete a membership while retaining activity history. |
| `GET` | `/api/clubs/{clubId}/member-roster` | Authorized, paged eligible-roster endpoint used by Activity Service. |
| `POST` | `/api/clubs/{clubId}/member-roster/resolve` | Resolve and validate a set of membership IDs in one query. |

Member-list query parameters: `search`, `status`, `role`, `sortBy`, `sortDirection`, `page`, and `pageSize` (`1..100`).

## Activity Service routes

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/activities/clubs/{clubId}/member-statistics` | Batch participation statistics for up to 500 members without N+1 queries. |
| `POST` | `/api/activities/clubs/{clubId}/member-statistics/detail` | One member's summary and paged activity history. |
| `GET` | `/api/clubs/{clubId}/activities/{activityId}/attendance` | Eligible roster with current attendance states and totals. |
| `PUT` | `/api/clubs/{clubId}/activities/{activityId}/attendance/{memberId}` | Upsert one attendance result. |
| `PUT` | `/api/clubs/{clubId}/activities/{activityId}/attendance` | Validate and save up to 500 results in one transaction. |

Attendance states are `NotMarked`, `Present`, `Absent`, `Excused`, and `Late`. Only `Present` counts as participation. Eligible activities start on or after the member's join time, start no later than now, and are not cancelled. The rate is `Present / Eligible * 100`, rounded to two decimal places; it is zero when there are no eligible activities.

## Schema upgrade

- EF migration: `20260722030350_AddClubMemberActivityManagement` adds membership soft-delete columns and query indexes.
- `ActivitySchemaUpgrader` performs the Activity database's existing idempotent startup migration pattern: attendance status, note, checker/timestamps, indexes, nullable future check-in time, and a restrictive attendance-history foreign key.

Apply the Club database migration manually when startup migration is disabled:

```powershell
dotnet ef database update --project src/Services/ClubService/ClubService.csproj --startup-project src/Services/ClubService/ClubService.csproj
```

## Verification

```powershell
dotnet build ClubReportHub.sln
dotnet test ClubReportHub.sln

cd C:\Users\kenfi\Desktop\PRN_FE
npm run build
```

The frontend routes are `/clubs/{clubId}/members` and `/clubs/{clubId}/attendance`.
