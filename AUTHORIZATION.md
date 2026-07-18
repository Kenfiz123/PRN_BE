# ClubReportHub authorization matrix

Backend authorization is authoritative. Frontend permissions only control visibility and navigation.

Each account has exactly one predefined system actor role. Club manager and treasurer capabilities are additionally derived from current club assignments, not by stacking multiple JWT roles.

| Actor | Allowed responsibilities |
|---|---|
| `ADMIN` | Emergency/super-admin access, system administration, and Student Affairs approvals |
| `SYSTEM_ADMIN` | Manage non-ADMIN users and predefined roles; access own dashboard, profile, and notifications |
| `STUDENT_AFFAIRS_ADMIN` | Review club applications, manage club records/manager assignments, approve reports, approve budgets and settlements, and export reports |
| `CLUB_MANAGER` | Manage assigned clubs, review membership requests, assign treasurers, manage activities, and author/review reports for assigned clubs |
| `TREASURER` | View and manage finance for clubs where the user has an approved treasurer membership |
| `CLUB_MEMBER` | View active clubs, request club membership, view approved reports, view activities, and register themself for activities |

## Enforcement rules

- `SYSTEM_ADMIN` is excluded from club, activity, report, and finance APIs.
- `SYSTEM_ADMIN` cannot grant itself `ADMIN`, create an `ADMIN`, or lock/change an existing `ADMIN` account.
- Student Affairs approval policies never include `SYSTEM_ADMIN`.
- Club-bound write operations require a current manager or treasurer assignment from `ClubService`; a JWT role alone is not sufficient.
- Club managers do not receive finance permissions automatically.
- Treasurers cannot create activities or author reports.
- Members can read only approved reports for clubs they belong to. Draft and review data is limited to its author, assigned club managers, and Student Affairs reviewers.
- Non-reviewers cannot access inactive clubs unless they are the assigned club manager.
- Notification access is scoped to the current user or one of the current user's roles. Only `ADMIN` can query across recipients.
