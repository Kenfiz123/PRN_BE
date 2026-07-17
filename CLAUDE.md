# ClubReportHub Backend

## Cấu trúc

```
backend/
├── src/
│   ├── Gateway/           # API Gateway (YARP)
│   ├── Services/          # Microservices
│   │   ├── AuthService/       # Authentication & Authorization
│   │   ├── ClubService/      # Club Management
│   │   ├── ActivityService/   # Activity Management
│   │   ├── ReportService/     # Report Management & KPI
│   │   ├── FinanceService/    # Finance Management
│   │   ├── ExportService/     # Export to PDF/Excel
│   │   └── NotificationService/ # Notifications
│   └── Shared/            # Shared libraries
│       └── ClubReportHub.Shared/
│           ├── Auth/           # JWT, Claims
│           ├── Messaging/      # RabbitMQ
│           └── Data/           # EF Core helpers
├── tests/
│   └── ClubReportHub.Tests/   # Unit & Integration tests
├── docker-compose.yml     # Docker orchestration
└── ClubReportHub.slnx    # Solution file
```

## Services

| Service | Port | Database | Description |
|---------|------|----------|-------------|
| API Gateway | 7000 | - | YARP reverse proxy |
| Auth | 5101 | Auth | Login, Register, JWT, Roles |
| Club | 5102 | Club | Clubs, Members, Applications |
| Activity | 5106 | Activity | Activities, Participants |
| Report | 5103 | Report | Reports, KPI, Deadlines |
| Finance | 5107 | Finance | Budget, Settlements |
| Export | 5104 | Export | PDF/Excel generation |
| Notification | 5105 | Notification | RabbitMQ consumer |

## Chạy Local

```bash
# Chạy với Docker
cd backend
docker-compose up -d

# Hoặc chạy local với dotnet
cd backend
dotnet restore
dotnet build
dotnet run --project src/Services/AuthService
dotnet run --project src/Services/ClubService
# ... các services khác
```

## Chạy Tests

```bash
cd backend
dotnet test
```

## Môi trường

Tạo `.env` file:
```
SQL_PASSWORD=YourPassword123!
JWT_SIGNING_KEY=your-secret-key-at-least-32-characters
```

## Database

- SQL Server 2022
- Entity Framework Core với migrations
- Mỗi service có database riêng
