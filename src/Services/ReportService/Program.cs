using System.Security.Claims;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using Grpc.Net.Client;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportService.Attachments;
using ReportService.Clients;
using ReportService.Contracts;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Models;
using ReportService.Options;
using ReportService.Services;

// Use types from KpiGrpcService project reference (KpiGrpcService.Protos namespace)
using KpiContract = ReportService.Contracts;

// Allow unencrypted HTTP/2 (h2c) for gRPC calls
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DefaultConnection is required");
builder.Services.AddDbContext<ReportDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.Configure<ReportAttachmentOptions>(builder.Configuration.GetSection(ReportAttachmentOptions.SectionName));
builder.Services.Configure<DemoDataOptions>(builder.Configuration.GetSection("DemoData"));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddRedisStreamEventBus(builder.Configuration);
builder.Services.AddHttpClient<FinanceWorkflowClient>(client =>
{
    var baseUrl = builder.Configuration["Services:FinanceService:BaseUrl"] ?? "http://localhost:5107";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<ActivityPublishingClient>(client =>
{
    var baseUrl = builder.Configuration["Services:ActivityService:BaseUrl"] ?? "http://localhost:5106";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

// gRPC client for KpiGrpcService
var kpiGrpcUrl = builder.Configuration.GetValue<string>("Services:KpiGrpcService:BaseUrl") ?? "http://localhost:5110";
builder.Services.AddSingleton(sp =>
{
    var channel = GrpcChannel.ForAddress(kpiGrpcUrl, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true }
    });
    return new KpiGrpcService.Protos.Client.KpiService.KpiServiceClient(channel);
});
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = true,
        QueuePollInterval = TimeSpan.FromSeconds(10)
    }));
builder.Services.AddHangfireServer();
builder.Services.AddScoped<ReportDeadlineJobs>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000", "http://localhost:5173"];
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new AllowAllDashboardAuthorizationFilter()]
});

app.MapHealthChecks("/health");
app.MapGet("/error", () => Results.Problem("An unexpected error occurred.")).AllowAnonymous();
app.MapGet("/", () => Results.Ok(new { service = "Report Service", status = "running" }));

var reports = app.MapGroup("/api/reports")
    .WithTags("Reports")
    .RequireAuthorization(AuthPolicies.BusinessAccess);

reports.MapGet("/", async (
    int? clubId,
    string? status,
    string? period,
    string? tag,
    int page,
    int pageSize,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    page = Math.Max(page, 1);
    pageSize = pageSize is <= 0 or > 100 ? 20 : pageSize;

    var query = db.Reports
        .Include(x => x.UploadedFile)
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .AsQueryable();

    var reviewer = IsReportReviewer(user);
    var financeVisibleClubIds = new HashSet<int>();
    if (!reviewer)
    {
        var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
        var managedClubIds = access.Where(x => x.CanManage).Select(x => x.ClubId).ToHashSet();
        financeVisibleClubIds = access.Where(x => x.CanManage || x.CanManageFinance).Select(x => x.ClubId).ToHashSet();
        var visibleClubIds = access.Where(x => x.CanView).Select(x => x.ClubId).ToHashSet();
        var userId = user.GetUserId();
        query = query.Where(x =>
            managedClubIds.Contains(x.ClubId)
            || x.CreatedByUserId == userId
            || (financeVisibleClubIds.Contains(x.ClubId)
                && x.ReportType == "FUTURE_EVENT"
                && x.Status != ReportStatuses.Draft)
            || (visibleClubIds.Contains(x.ClubId) && x.Status == ReportStatuses.Approved));
    }

    if (clubId.HasValue)
    {
        query = query.Where(x => x.ClubId == clubId);
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(x => x.Status == status);
    }

    if (!string.IsNullOrWhiteSpace(period))
    {
        query = query.Where(x => x.Period == period);
    }

    if (!string.IsNullOrWhiteSpace(tag))
    {
        query = query.Where(x => x.Tag == tag);
    }

    var total = await query.CountAsync();
    var rows = await query
        .OrderByDescending(x => x.UpdatedAtUtc)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();

    return Results.Ok(new
    {
        total,
        page,
        pageSize,
        items = rows.Select(report => ToResponse(
            report,
            !IsFutureEventReportModel(report) || reviewer || financeVisibleClubIds.Contains(report.ClubId)))
    });
});

reports.MapGet("/summary", async (
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var query = db.Reports.AsQueryable();
    if (!IsReportReviewer(user))
    {
        var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
        var managedClubIds = access.Where(x => x.CanManage).Select(x => x.ClubId).ToHashSet();
        var visibleClubIds = access.Where(x => x.CanView).Select(x => x.ClubId).ToHashSet();
        var userId = user.GetUserId();
        query = query.Where(x =>
            managedClubIds.Contains(x.ClubId)
            || x.CreatedByUserId == userId
            || (visibleClubIds.Contains(x.ClubId) && x.Status == ReportStatuses.Approved));
    }

    var allReports = await query.ToListAsync(cancellationToken);
    return Results.Ok(new ReportSummaryResponse(
        allReports.Count,
        allReports.Count(x => x.Status == ReportStatuses.Draft),
        allReports.Count(x => x.Status is ReportStatuses.Submitted or ReportStatuses.AwaitingFinance),
        allReports.Count(x => x.Status == ReportStatuses.UnderReview),
        allReports.Count(x => x.Status == ReportStatuses.Approved),
        allReports.Count(x => x.Status == ReportStatuses.Rejected),
        allReports.Count(x => (x.Status == ReportStatuses.Draft || x.Status == ReportStatuses.Rejected) && x.DueDate < today)));
});

reports.MapGet("/aggregate", async (string? period, ReportDbContext db) =>
{
    var query = db.Reports.Include(x => x.Details).Where(x => x.Status == ReportStatuses.Approved);
    if (!string.IsNullOrWhiteSpace(period))
    {
        query = query.Where(x => x.Period == period);
    }

    var approved = await query.ToListAsync();
    var clubs = approved
        .GroupBy(x => new { x.ClubId, x.ClubName })
        .Select(group => new ClubAggregationRow(
            group.Key.ClubId,
            group.Key.ClubName,
            group.Count(),
            group.Sum(x => x.Details.Count),
            group.Sum(x => x.Details.Sum(d => d.ParticipantCount))))
        .OrderByDescending(x => x.Participants)
        .ToArray();

    return Results.Ok(new AggregationResponse(
        period,
        approved.Count,
        approved.Sum(x => x.Details.Count),
        approved.Sum(x => x.Details.Sum(d => d.ParticipantCount)),
        clubs));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

var kpis = app.MapGroup("/api/kpis")
    .WithTags("KPI")
    .RequireAuthorization(AuthPolicies.BusinessAccess);
kpis.MapGet("/rules", () => Results.Ok(new[]
{
    new KpiContract.KpiRuleResponse("APPROVED_REPORT", "Approved report", 50, "Each approved period report adds KPI points."),
    new KpiContract.KpiRuleResponse("ACTIVITY", "Reported activity", 5, "Each approved activity detail contributes operational KPI."),
    new KpiContract.KpiRuleResponse("PARTICIPATION", "Participant engagement", 0.1m, "Each participant in approved activities contributes 0.1 point."),
    new KpiContract.KpiRuleResponse("REJECTED_REPORT", "Rejected report penalty", -10, "Rejected reports reduce KPI until revised and approved."),
    new KpiContract.KpiRuleResponse("OVERDUE_REPORT", "Overdue report penalty", -20, "Draft or rejected reports past due date reduce KPI.")
}));

kpis.MapGet("/leaderboard", async (string? period, ReportDbContext db, KpiGrpcService.Protos.Client.KpiService.KpiServiceClient grpcClient, ILogger<Program> logger, CancellationToken httpCancellationToken) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var query = db.Reports.Include(x => x.Details).AsQueryable();
    if (!string.IsNullOrWhiteSpace(period))
    {
        query = query.Where(x => x.Period == period);
    }

    var reportsForKpi = await query.ToListAsync();
    var clubMetrics = reportsForKpi
        .GroupBy(x => new { x.ClubId, x.ClubName })
        .Select(group =>
        {
            var approved = group.Where(x => x.Status == ReportStatuses.Approved).ToArray();
            var rejectedCount = group.Count(x => x.Status == ReportStatuses.Rejected);
            var overdueCount = group.Count(x => (x.Status == ReportStatuses.Draft || x.Status == ReportStatuses.Rejected) && x.DueDate < today);
            var activityCount = approved.Sum(x => x.Details.Count);
            var participants = approved.Sum(x => x.Details.Sum(d => d.ParticipantCount));
            var points = approved.Length * 50m + activityCount * 5m + participants * 0.1m - rejectedCount * 10m - overdueCount * 20m;
            return new
            {
                group.Key.ClubId,
                group.Key.ClubName,
                Points = Math.Max(0, decimal.Round(points, 2)),
                ApprovedReports = approved.Length,
                Activities = activityCount,
                Participants = participants,
                RejectedReports = rejectedCount,
                OverdueReports = overdueCount
            };
        })
        .OrderByDescending(x => x.Points)
        .ThenBy(x => x.ClubName)
        .ToArray();

    // Call gRPC service for each club to get enriched KPI result with rating
    var ranked = new List<KpiContract.KpiLeaderboardRow>();
    var correlationId = Guid.NewGuid().ToString();

    // Deadline: 5 seconds per club, overall bounded by HTTP request CancellationToken
    using var grpcCts = CancellationTokenSource.CreateLinkedTokenSource(httpCancellationToken);
    grpcCts.CancelAfter(TimeSpan.FromSeconds(5));

    foreach (var metric in clubMetrics)
    {
        grpcCts.Token.ThrowIfCancellationRequested();
        try
        {
            var grpcRequest = new KpiGrpcService.Protos.Client.KpiClubRequest
            {
                ClubId = metric.ClubId,
                ClubName = metric.ClubName,
                Period = period ?? "",
                ApprovedReports = metric.ApprovedReports,
                ActivityCount = metric.Activities,
                ParticipantCount = metric.Participants,
                RejectedReports = metric.RejectedReports,
                OverdueReports = metric.OverdueReports,
                CorrelationId = correlationId
            };

            logger.LogInformation(
                "REST calling gRPC CalculateClubKpi for ClubId: {ClubId}, CorrelationId: {CorrelationId}",
                metric.ClubId, correlationId);

            var grpcCallOptions = new Grpc.Core.CallOptions(deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: grpcCts.Token);
            var grpcResponse = await grpcClient.CalculateClubKpiAsync(grpcRequest, grpcCallOptions);

            logger.LogInformation(
                "gRPC response received for ClubId: {ClubId}, Score: {Score}, Rating: {Rating}, CorrelationId: {CorrelationId}",
                grpcResponse.ClubId, grpcResponse.TotalScore, grpcResponse.Rating, correlationId);

            ranked.Add(new KpiContract.KpiLeaderboardRow(
                ranked.Count + 1,
                metric.ClubId,
                metric.ClubName,
                (decimal)grpcResponse.TotalScore,
                metric.ApprovedReports,
                metric.Activities,
                metric.Participants,
                metric.RejectedReports,
                metric.OverdueReports));
        }
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogWarning(ex,
                "gRPC call failed for ClubId: {ClubId}. Falling back to local calculation. CorrelationId: {CorrelationId}, gRPC Status: {GrpcStatus}",
                metric.ClubId, correlationId, ex.StatusCode);
            // Fallback: use local calculation when gRPC is unavailable
            ranked.Add(new KpiContract.KpiLeaderboardRow(
                ranked.Count + 1,
                metric.ClubId,
                metric.ClubName,
                metric.Points,
                metric.ApprovedReports,
                metric.Activities,
                metric.Participants,
                metric.RejectedReports,
                metric.OverdueReports));
        }
    }

    return Results.Ok(new KpiContract.KpiLeaderboardResponse(period, DateTimeOffset.UtcNow, ranked));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

reports.MapGet("/{id:int}", async (
    int id,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.UploadedFile)
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    if (report is null)
    {
        return Results.NotFound();
    }

    if (!await CanReadReportAsync(report, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    var includeFinance = await CanViewFinanceAsync(report, user, clubAccess, httpContext, cancellationToken);
    return Results.Ok(ToResponse(report, includeFinance));
});

reports.MapPost("/", async (
    CreateReportRequest request,
    ReportDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var tag = NormalizeReportTag(request.Tag, request.ReportType);
    var reportType = NormalizeReportType(request.ReportType, tag);
    var isFutureEvent = IsFutureEventReport(reportType);
    var authorAccess = await GetAuthorAccessAsync(
        request.ClubId,
        tag,
        reportType,
        clubAccess,
        httpContext,
        cancellationToken);
    if (authorAccess is null)
    {
        return Results.Forbid();
    }

    var period = request.Period.Trim();

    if (!isFutureEvent
        && await db.Reports.AnyAsync(x => x.ClubId == request.ClubId && x.Period == period && x.Tag == tag, cancellationToken))
    {
        return Results.Conflict(new { message = "A report already exists for this club, period, and tag." });
    }

    var futureValidation = ValidateFutureEventDetails(reportType, request.Details);
    if (futureValidation is not null)
    {
        return Results.BadRequest(new { message = futureValidation });
    }

    var deadline = await db.ReportingDeadlines.FirstOrDefaultAsync(x => x.Period == period, cancellationToken);
    var dueDate = deadline?.DueDate ?? request.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));

    var report = new Report
    {
        ClubId = request.ClubId,
        ClubName = authorAccess.ClubName,
        Period = period,
        ReportType = reportType,
        Tag = tag,
        DueDate = dueDate,
        CreatedByUserId = user.GetUserId(),
        ExecutiveSummary = request.ExecutiveSummary?.Trim(),
        Achievements = request.Achievements?.Trim(),
        Challenges = request.Challenges?.Trim(),
        Recommendations = request.Recommendations?.Trim(),
        NextPeriodPlan = request.NextPeriodPlan?.Trim(),
        Details = request.Details.Select(detail => ToDetail(detail, includeBudget: !isFutureEvent)).ToList()
    };

    db.Reports.Add(report);
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "Create", user.GetUserId(), "Report draft created.", cancellationToken);

    var recipientUserIds = authorAccess.ManagerUserIds
        .Append(user.GetUserId())
        .Where(id => id > 0)
        .Distinct()
        .ToArray();
    await eventBus.PublishAsync(new ReportCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        report.CreatedByUserId,
        recipientUserIds), EventRoutingKeys.ReportCreated, cancellationToken);

    return Results.Created($"/api/reports/{report.Id}", ToResponse(report));
});

reports.MapPost("/upload", async (
    HttpContext httpContext,
    IConfiguration config,
    ReportDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.Request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Request content type must be multipart/form-data." });
    }

    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null)
    {
        return Results.BadRequest(new { message = "Vui lòng chọn tệp báo cáo để tải lên." });
    }

    if (!int.TryParse(form["clubId"], out var clubId) || clubId <= 0)
    {
        return Results.BadRequest(new { message = "ClubId không hợp lệ." });
    }

    var period = form["period"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(period))
    {
        return Results.BadRequest(new { message = "Kỳ báo cáo là bắt buộc." });
    }

    var reportType = form["reportType"].ToString().Trim();
    var note = form["note"].ToString().Trim();

    var validation = ValidateUploadedReportFile(file);
    if (!validation.IsValid)
    {
        return Results.BadRequest(new { message = validation.ErrorMessage });
    }

    var tag = NormalizeReportTag(reportType, reportType);
    var normReportType = NormalizeReportType(reportType, tag);
    var authorAccess = await GetAuthorAccessAsync(
        clubId,
        tag,
        normReportType,
        clubAccess,
        httpContext,
        cancellationToken);

    if (authorAccess is null)
    {
        return Results.Forbid();
    }

    if (await db.Reports.AnyAsync(x => x.ClubId == clubId && x.Period == period && x.Tag == tag, cancellationToken))
    {
        return Results.Conflict(new { message = "A report already exists for this club, period, and tag." });
    }

    var deadline = await db.ReportingDeadlines.FirstOrDefaultAsync(x => x.Period == period, cancellationToken);
    var dueDate = deadline?.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));

    var savedFile = await SaveUploadedReportFileAsync(file, clubId, config, cancellationToken);

    var report = new Report
    {
        ClubId = clubId,
        ClubName = authorAccess.ClubName,
        Period = period,
        ReportType = normReportType,
        Tag = tag,
        DueDate = dueDate,
        CreatedByUserId = user.GetUserId(),
        ContentSource = ReportContentSources.UploadedFile,
        Status = ReportStatuses.Draft,
        ExecutiveSummary = string.IsNullOrWhiteSpace(note) ? null : note
    };

    var uploadedFile = new ReportUploadedFile
    {
        OriginalFileName = savedFile.OriginalFileName,
        StoredFileName = savedFile.StoredFileName,
        ContentType = validation.ContentType,
        FileExtension = Path.GetExtension(savedFile.OriginalFileName).ToLowerInvariant(),
        SizeBytes = savedFile.SizeBytes,
        StoragePath = savedFile.StoragePath,
        Checksum = savedFile.Checksum,
        UploadedByUserId = user.GetUserId(),
        UploadedAtUtc = DateTimeOffset.UtcNow,
        IsActive = true,
        Report = report
    };

    await ReportPreviewGenerator.GeneratePreviewAsync(uploadedFile, config, cancellationToken);
    report.UploadedFile = uploadedFile;
    db.Reports.Add(report);
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "Upload", user.GetUserId(), "Report file uploaded as draft.", cancellationToken);

    return Results.Created($"/api/reports/{report.Id}", ToResponse(report));
}).DisableAntiforgery();

reports.MapGet("/{reportId:int}/uploaded-file", async (
    int reportId,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.UploadedFile)
        .FirstOrDefaultAsync(x => x.Id == reportId, cancellationToken);

    if (report is null)
    {
        return Results.NotFound(new { message = "Không tìm thấy báo cáo." });
    }

    if (!await CanReadReportAsync(report, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.UploadedFile is null || !report.UploadedFile.IsActive)
    {
        return Results.NotFound(new { message = "Báo cáo này không có file đính kèm hoặc file đã bị xóa." });
    }

    var isDownloadAvailable = File.Exists(report.UploadedFile.StoragePath);
    var isPreviewAvailable = !string.IsNullOrEmpty(report.UploadedFile.PreviewStoragePath) && File.Exists(report.UploadedFile.StoragePath);

    return Results.Ok(new ReportUploadedFileResponse(
        report.UploadedFile.Id,
        report.UploadedFile.OriginalFileName,
        report.UploadedFile.ContentType,
        report.UploadedFile.FileExtension,
        report.UploadedFile.SizeBytes,
        report.UploadedFile.UploadedAtUtc,
        report.UploadedFile.UploadedByUserId,
        isDownloadAvailable,
        report.UploadedFile.PreviewStatus ?? "Available",
        isPreviewAvailable,
        report.UploadedFile.PreviewErrorMessage));
});

reports.MapGet("/{reportId:int}/uploaded-file/preview", async (
    int reportId,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.UploadedFile)
        .FirstOrDefaultAsync(x => x.Id == reportId, cancellationToken);

    if (report is null)
    {
        return Results.NotFound(new { message = "Không tìm thấy báo cáo." });
    }

    if (!await CanReadReportAsync(report, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.UploadedFile is null || !report.UploadedFile.IsActive || report.UploadedFile.ReportId != report.Id)
    {
        return Results.NotFound(new { message = "Báo cáo này không có file đính kèm." });
    }

    var uploadedFile = report.UploadedFile;

    if (uploadedFile.PreviewStatus is null || uploadedFile.PreviewStatus == "None" || string.IsNullOrEmpty(uploadedFile.PreviewStoragePath) || !File.Exists(uploadedFile.PreviewStoragePath))
    {
        await ReportPreviewGenerator.GeneratePreviewAsync(uploadedFile, config, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    if (uploadedFile.PreviewStatus == "Pending")
    {
        return Results.Conflict(new { message = "Quá trình tạo bản xem trước đang được xử lý." });
    }

    if (uploadedFile.PreviewStatus == "Failed")
    {
        return Results.BadRequest(new { message = uploadedFile.PreviewErrorMessage ?? "Không thể tạo bản xem trước cho file này." });
    }

    if (uploadedFile.PreviewStatus == "Unsupported" || string.IsNullOrEmpty(uploadedFile.PreviewStoragePath) || !File.Exists(uploadedFile.PreviewStoragePath))
    {
        return Results.BadRequest(new { message = "Định dạng file không hỗ trợ xem trước trực tiếp." });
    }

    var previewPath = Path.GetFullPath(uploadedFile.PreviewStoragePath);
    var previewsDir = Path.GetFullPath(config["Uploads:PreviewStoragePath"]
        ?? Path.Combine(AppContext.BaseDirectory, "report-previews"));
    var uploadsDir = Path.GetFullPath(config["Uploads:StoragePath"]
        ?? Path.Combine(AppContext.BaseDirectory, "report-uploads"));

    if (!previewPath.StartsWith(previewsDir, StringComparison.OrdinalIgnoreCase) &&
        !previewPath.StartsWith(uploadsDir, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Đường dẫn file xem trước không hợp lệ." });
    }

    var contentType = uploadedFile.PreviewContentType ?? "application/pdf";
    var fileName = Path.GetFileName(previewPath);

    httpContext.Response.Headers.Append("Content-Disposition", $"inline; filename=\"{fileName}\"");
    return Results.File(previewPath, contentType, enableRangeProcessing: true);
});

reports.MapGet("/{reportId:int}/uploaded-file/download", async (
    int reportId,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.UploadedFile)
        .FirstOrDefaultAsync(x => x.Id == reportId, cancellationToken);

    if (report is null)
    {
        return Results.NotFound(new { message = "Không tìm thấy báo cáo." });
    }

    if (!await CanReadReportAsync(report, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.UploadedFile is null || !report.UploadedFile.IsActive)
    {
        return Results.NotFound(new { message = "Báo cáo này không có file đính kèm." });
    }

    if (!File.Exists(report.UploadedFile.StoragePath))
    {
        return Results.NotFound(new { message = "Tệp tin vật lý không còn tồn tại trên máy chủ." });
    }

    return Results.File(
        report.UploadedFile.StoragePath,
        report.UploadedFile.ContentType,
        report.UploadedFile.OriginalFileName,
        enableRangeProcessing: true);
});

reports.MapPut("/{reportId:int}/uploaded-file", async (
    int reportId,
    HttpContext httpContext,
    IConfiguration config,
    ReportDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.UploadedFile)
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .FirstOrDefaultAsync(x => x.Id == reportId, cancellationToken);

    if (report is null)
    {
        return Results.NotFound(new { message = "Không tìm thấy báo cáo." });
    }

    if (!await CanAuthorReportsAsync(report.ClubId, report.Tag, report.ReportType, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Chỉ có thể thay đổi tệp báo cáo khi ở trạng thái Nháp hoặc Thất bại/Yêu cầu sửa." });
    }

    if (!httpContext.Request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Request content type must be multipart/form-data." });
    }

    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null)
    {
        return Results.BadRequest(new { message = "Vui lòng chọn tệp báo cáo mới." });
    }

    var validation = ValidateUploadedReportFile(file);
    if (!validation.IsValid)
    {
        return Results.BadRequest(new { message = validation.ErrorMessage });
    }

    var savedFile = await SaveUploadedReportFileAsync(file, report.ClubId, config, cancellationToken);

    if (report.UploadedFile is not null)
    {
        report.UploadedFile.IsActive = false;
    }

    var newUploadedFile = new ReportUploadedFile
    {
        ReportId = report.Id,
        OriginalFileName = savedFile.OriginalFileName,
        StoredFileName = savedFile.StoredFileName,
        ContentType = validation.ContentType,
        FileExtension = Path.GetExtension(savedFile.OriginalFileName).ToLowerInvariant(),
        SizeBytes = savedFile.SizeBytes,
        StoragePath = savedFile.StoragePath,
        Checksum = savedFile.Checksum,
        UploadedByUserId = user.GetUserId(),
        UploadedAtUtc = DateTimeOffset.UtcNow,
        IsActive = true
    };

    report.UploadedFile = newUploadedFile;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "ReplaceUploadedFile", user.GetUserId(), "Uploaded report file replaced.", cancellationToken);

    return Results.Ok(ToResponse(report));
}).DisableAntiforgery();

reports.MapDelete("/{reportId:int}/uploaded-file", async (
    int reportId,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.UploadedFile)
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .FirstOrDefaultAsync(x => x.Id == reportId, cancellationToken);

    if (report is null)
    {
        return Results.NotFound(new { message = "Không tìm thấy báo cáo." });
    }

    if (!await CanAuthorReportsAsync(report.ClubId, report.Tag, report.ReportType, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Chỉ có thể xóa tệp báo cáo khi ở trạng thái Nháp hoặc Thất bại." });
    }

    if (report.UploadedFile is not null)
    {
        report.UploadedFile.IsActive = false;
        report.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await AddAuditAsync(db, report.Id, "DeleteUploadedFile", user.GetUserId(), "Uploaded report file deleted.", cancellationToken);
    }

    return Results.Ok(ToResponse(report));
});

reports.MapPut("/{id:int}", async (
    int id,
    UpdateReportRequest request,
    ReportDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports.Include(x => x.Details).Include(x => x.Attachments).Include(x => x.Feedback).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Only draft or rejected reports can be edited." });
    }

    var period = request.Period.Trim();
    var tag = NormalizeReportTag(request.Tag, request.ReportType);
    var reportType = NormalizeReportType(request.ReportType, tag);
    var isFutureEvent = IsFutureEventReport(reportType);
    if (report.CreatedByUserId != user.GetUserId()
        || !await CanAuthorReportsAsync(
            report.ClubId,
            tag,
            reportType,
            clubAccess,
            httpContext,
            cancellationToken))
    {
        return Results.Forbid();
    }

    if (!isFutureEvent
        && await db.Reports.AnyAsync(x => x.Id != id && x.ClubId == report.ClubId && x.Period == period && x.Tag == tag))
    {
        return Results.Conflict(new { message = "Another report already uses this club, period, and tag." });
    }

    var futureValidation = ValidateFutureEventDetails(reportType, request.Details);
    if (futureValidation is not null)
    {
        return Results.BadRequest(new { message = futureValidation });
    }

    var deadline = await db.ReportingDeadlines.FirstOrDefaultAsync(x => x.Period == period, cancellationToken);
    if (deadline is not null)
    {
        report.DueDate = deadline.DueDate;
    }
    else if (request.DueDate.HasValue)
    {
        report.DueDate = request.DueDate.Value;
    }

    report.Period = period;
    report.Tag = tag;
    report.ReportType = reportType;
    report.ExecutiveSummary = request.ExecutiveSummary?.Trim();
    report.Achievements = request.Achievements?.Trim();
    report.Challenges = request.Challenges?.Trim();
    report.Recommendations = request.Recommendations?.Trim();
    report.NextPeriodPlan = request.NextPeriodPlan?.Trim();
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    report.Version++;
    db.ReportDetails.RemoveRange(report.Details);
    report.Details = request.Details.Select(detail => ToDetail(detail, includeBudget: !isFutureEvent)).ToList();
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "Update", user.GetUserId(), "Report draft updated.", cancellationToken);
    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/attachments", async (
    int id,
    AddAttachmentRequest request,
    ReportDbContext db,
    IOptions<ReportAttachmentOptions> attachmentOptions,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports.Include(x => x.Details).Include(x => x.Attachments).Include(x => x.Feedback).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.CreatedByUserId != user.GetUserId()
        || !await CanAuthorReportsAsync(
            report.ClubId,
            report.Tag,
            report.ReportType,
            clubAccess,
            httpContext,
            cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Attachments can only be changed on draft or rejected reports." });
    }

    var validation = ReportAttachmentPolicy.Validate(request.FileName, request.ContentType, request.SizeBytes, attachmentOptions.Value);
    if (!validation.Succeeded)
    {
        return Results.BadRequest(new { message = validation.ErrorMessage });
    }

    if (request.ReportDetailId.HasValue && report.Details.All(x => x.Id != request.ReportDetailId.Value))
    {
        return Results.BadRequest(new { message = "Report detail does not belong to this report." });
    }

    var safeName = ReportAttachmentPolicy.GetSafeFileName(request.FileName);
    report.Attachments.Add(new ReportAttachment
    {
        ReportDetailId = request.ReportDetailId,
        FileName = safeName,
        ContentType = request.ContentType,
        SizeBytes = request.SizeBytes,
        StoragePath = request.StoragePath.Trim()
    });
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    await AddAuditAsync(db, report.Id, "Attachment", user.GetUserId(), $"Attachment metadata added: {safeName}");
    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/attachments/upload", async (
    int id,
    [FromForm] IFormFile? file,
    [FromForm] int? reportDetailId,
    ReportDbContext db,
    IOptions<ReportAttachmentOptions> attachmentOptions,
    IWebHostEnvironment environment,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.CreatedByUserId != user.GetUserId()
        || !await CanAuthorReportsAsync(
            report.ClubId,
            report.Tag,
            report.ReportType,
            clubAccess,
            httpContext,
            cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Attachments can only be changed on draft or rejected reports." });
    }

    if (reportDetailId.HasValue && report.Details.All(x => x.Id != reportDetailId.Value))
    {
        return Results.BadRequest(new { message = "Report detail does not belong to this report." });
    }

    if (file is null)
    {
        return Results.BadRequest(new { message = "Evidence file is required." });
    }

    var validation = ReportAttachmentPolicy.Validate(file.FileName, file.ContentType, file.Length, attachmentOptions.Value);
    if (!validation.Succeeded)
    {
        return Results.BadRequest(new { message = validation.ErrorMessage });
    }

    var safeName = ReportAttachmentPolicy.GetSafeFileName(file.FileName);
    var storageRoot = ReportAttachmentPolicy.ResolveStorageRoot(attachmentOptions.Value.StoragePath, environment.ContentRootPath);
    var reportFolder = Path.Combine(storageRoot, report.Id.ToString());

    // Validate path stays within storage root (prevent path traversal attacks)
    var normalizedFolder = Path.GetFullPath(reportFolder);
    if (!normalizedFolder.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Invalid storage path." });
    }

    Directory.CreateDirectory(reportFolder);

    var storedFileName = ReportAttachmentPolicy.CreateStoredFileName(safeName);
    var filePath = Path.Combine(reportFolder, storedFileName);

    // Double-check the final path is within storage root
    var normalizedFilePath = Path.GetFullPath(filePath);
    if (!normalizedFilePath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Invalid file path." });
    }

    await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        await file.CopyToAsync(stream, cancellationToken);
    }

    report.Attachments.Add(new ReportAttachment
    {
        ReportDetailId = reportDetailId,
        FileName = safeName,
        ContentType = file.ContentType,
        SizeBytes = file.Length,
        StoragePath = filePath
    });
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "AttachmentUpload", user.GetUserId(), $"Evidence uploaded: {safeName}", cancellationToken);
    return Results.Ok(ToResponse(report));
})
.Accepts<IFormFile>("multipart/form-data")
.DisableAntiforgery();

reports.MapGet("/{id:int}/attachments/{attachmentId:int}/download", async (
    int id,
    int attachmentId,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .AsNoTracking()
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (!await CanReadReportAsync(report, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    var attachment = await db.ReportAttachments.AsNoTracking()
        .FirstOrDefaultAsync(x => x.ReportId == id && x.Id == attachmentId, cancellationToken);
    if (attachment is null || !File.Exists(attachment.StoragePath))
    {
        return Results.NotFound(new { message = "Attachment file is not available." });
    }

    return Results.File(attachment.StoragePath, attachment.ContentType, attachment.FileName);
});

reports.MapPost("/{id:int}/submit", async (
    int id,
    ReportDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.UploadedFile)
        .Include(x => x.Feedback)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    var authorAccess = await GetAuthorAccessAsync(
        report.ClubId,
        report.Tag,
        report.ReportType,
        clubAccess,
        httpContext,
        cancellationToken);
    var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
        report,
        user.GetUserId(),
        authorAccess is not null);
    if (isForbidden)
    {
        return Results.Forbid();
    }
    if (!isValid)
    {
        return Results.BadRequest(new { message = errorMessage });
    }

    var isFutureEvent = IsFutureEventReportModel(report);
    if (isFutureEvent && (authorAccess.TreasurerUserIds?.Count ?? 0) == 0)
    {
        return Results.BadRequest(new { message = "Assign at least one club treasurer before submitting a future event report." });
    }

    report.Status = isFutureEvent
        ? ReportStatuses.AwaitingFinance
        : authorAccess.CanManage ? ReportStatuses.UnderReview : ReportStatuses.Submitted;
    report.SubmittedAtUtc = DateTimeOffset.UtcNow;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "Submit", user.GetUserId(), "Report submitted for review.", cancellationToken);

    await eventBus.PublishAsync(new ReportSubmittedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        user.GetUserId(),
        report.Status,
        !isFutureEvent && report.Status == ReportStatuses.Submitted
            ? authorAccess.ManagerUserIds.Cast<int?>().FirstOrDefault()
            : null,
        isFutureEvent ? "FinanceReview" : "Standard",
        isFutureEvent ? authorAccess.TreasurerUserIds : null), EventRoutingKeys.ReportSubmitted, cancellationToken);

    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/link-budget", async (
    int id,
    LinkFutureEventBudgetRequest request,
    ReportDbContext db,
    IEventBus eventBus,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null) return Results.NotFound();
    if (!IsFutureEventReportModel(report))
    {
        return Results.BadRequest(new { message = "Only future event reports can receive a linked budget." });
    }

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == report.ClubId && x.CanManageFinance);
    if (access is null) return Results.Forbid();

    if (report.BudgetProposalId == request.BudgetProposalId
        && report.Status is ReportStatuses.Submitted or ReportStatuses.UnderReview or ReportStatuses.Approved)
    {
        return Results.Ok(ToResponse(report));
    }

    if (report.Status != ReportStatuses.AwaitingFinance)
    {
        return Results.BadRequest(new { message = "This future event report is not awaiting a budget." });
    }

    report.BudgetProposalId = request.BudgetProposalId;
    report.BudgetRequestedAmount = request.RequestedAmount;
    report.BudgetApprovedAmount = null;
    report.BudgetDescription = request.Description.Trim();
    report.FinanceSubmittedAtUtc = DateTimeOffset.UtcNow;
    report.Status = ReportStatuses.Submitted;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "FinanceLinked", httpContext.User.GetUserId(), "Treasurer submitted the linked event budget.", cancellationToken);

    await eventBus.PublishAsync(new ReportSubmittedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        httpContext.User.GetUserId(),
        report.Status,
        null,
        "ManagerReview",
        access.ManagerUserIds), EventRoutingKeys.ReportSubmitted, cancellationToken);

    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/review", async (
    int id,
    ReportDbContext db,
    IEventBus eventBus,
    FinanceWorkflowClient financeWorkflow,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports.Include(x => x.Details).Include(x => x.Attachments).Include(x => x.Feedback).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.Status != ReportStatuses.Submitted)
    {
        return Results.BadRequest(new { message = "Only submitted reports can enter review." });
    }

    var isFutureEvent = IsFutureEventReportModel(report);
    // A manager may approve their own event content only when reviewing the budget
    // independently submitted by the club treasurer.
    if (!isFutureEvent && report.CreatedByUserId == user.GetUserId())
    {
        return Results.Forbid();
    }

    // Must have club management permission
    if (!await CanManageClubAsync(report.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (isFutureEvent)
    {
        if (!report.BudgetProposalId.HasValue || !report.BudgetRequestedAmount.HasValue)
        {
            return Results.BadRequest(new { message = "The treasurer must submit the event budget before club owner review." });
        }

        var finance = await financeWorkflow.ManagerApproveAsync(
            report.BudgetProposalId.Value,
            "Approved with the combined future event report.",
            httpContext.GetBearerToken(),
            cancellationToken);
        if (finance is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    report.Status = ReportStatuses.UnderReview;
    report.ReviewedByUserId = user.GetUserId();
    report.ReviewedAtUtc = DateTimeOffset.UtcNow;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "ManagerReview", user.GetUserId(), "Report forwarded to Student Affairs for final approval.", cancellationToken);
    await eventBus.PublishAsync(new ReportSubmittedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        user.GetUserId(),
        report.Status,
        null,
        isFutureEvent ? "FinalReview" : "Standard"), EventRoutingKeys.ReportSubmitted, cancellationToken);
    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/approve", async (
    int id,
    ReviewRequest request,
    ReportDbContext db,
    IEventBus eventBus,
    FinanceWorkflowClient financeWorkflow,
    ActivityPublishingClient activityPublishing,
    ClaimsPrincipal user,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports.Include(x => x.Details).Include(x => x.Attachments).Include(x => x.Feedback).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.Status != ReportStatuses.UnderReview)
    {
        return Results.BadRequest(new { message = "Only reports forwarded by the club manager can be approved." });
    }

    if (IsFutureEventReportModel(report))
    {
        if (!report.BudgetProposalId.HasValue || !report.BudgetRequestedAmount.HasValue || report.Details.Count != 1)
        {
            return Results.BadRequest(new { message = "The future event package is incomplete." });
        }

        var finance = await financeWorkflow.FinalApproveAsync(
            report.BudgetProposalId.Value,
            report.BudgetRequestedAmount.Value,
            "Approved with the combined future event report.",
            httpContext.GetBearerToken(),
            cancellationToken);
        if (finance is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var detail = report.Details.Single();
        var published = await activityPublishing.PublishAsync(new
        {
            reportId = report.Id,
            reportDetailId = detail.Id,
            report.ClubId,
            report.ClubName,
            title = detail.ActivityName,
            detail.Description,
            detail.ActivityDate,
            location = string.IsNullOrWhiteSpace(detail.Location) ? "To be announced" : detail.Location
        }, httpContext.GetBearerToken(), cancellationToken);
        if (published is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        report.BudgetApprovedAmount = finance.ApprovedAmount ?? report.BudgetRequestedAmount;
        report.PublishedActivityId = published.Id;
    }

    report.Status = ReportStatuses.Approved;
    report.ReviewedByUserId = user.GetUserId();
    report.ReviewedAtUtc = DateTimeOffset.UtcNow;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    report.Feedback.Add(new ReportFeedback
    {
        ReviewerUserId = user.GetUserId(),
        ReviewerName = user.GetDisplayName(),
        Decision = ReportStatuses.Approved,
        Message = string.IsNullOrWhiteSpace(request.Feedback) ? "The report has been approved." : request.Feedback.Trim()
    });
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "Approve", user.GetUserId(), "Report approved.", cancellationToken);

    await eventBus.PublishAsync(new ReportApprovedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        user.GetUserId(),
        report.CreatedByUserId), EventRoutingKeys.ReportApproved, cancellationToken);

    return Results.Ok(ToResponse(report));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

reports.MapPost("/{id:int}/reject", async (
    int id,
    ReviewRequest request,
    ReportDbContext db,
    IEventBus eventBus,
    FinanceWorkflowClient financeWorkflow,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var feedback = string.IsNullOrWhiteSpace(request.Feedback) ? "Please provide the missing information and resubmit the report." : request.Feedback.Trim();
    var report = await db.Reports.Include(x => x.Details).Include(x => x.Attachments).Include(x => x.Feedback).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.Status is not (ReportStatuses.Submitted or ReportStatuses.UnderReview))
    {
        return Results.BadRequest(new { message = "Only submitted or under-review reports can be rejected." });
    }

    var canReject = report.Status == ReportStatuses.Submitted
        ? report.CreatedByUserId != user.GetUserId()
            && await CanManageClubAsync(report.ClubId, clubAccess, httpContext, cancellationToken)
        : IsReportReviewer(user);
    if (!canReject)
    {
        return Results.Forbid();
    }

    if (IsFutureEventReportModel(report) && report.BudgetProposalId.HasValue)
    {
        var finance = report.Status == ReportStatuses.Submitted
            ? await financeWorkflow.ManagerRejectAsync(
                report.BudgetProposalId.Value,
                feedback,
                httpContext.GetBearerToken(),
                cancellationToken)
            : await financeWorkflow.FinalRejectAsync(
                report.BudgetProposalId.Value,
                feedback,
                httpContext.GetBearerToken(),
                cancellationToken);
        if (finance is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        report.BudgetProposalId = null;
        report.BudgetRequestedAmount = null;
        report.BudgetApprovedAmount = null;
        report.BudgetDescription = null;
        report.FinanceSubmittedAtUtc = null;
    }

    report.Status = ReportStatuses.Rejected;
    report.ReviewedByUserId = user.GetUserId();
    report.ReviewedAtUtc = DateTimeOffset.UtcNow;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    report.Feedback.Add(new ReportFeedback
    {
        ReviewerUserId = user.GetUserId(),
        ReviewerName = user.GetDisplayName(),
        Decision = ReportStatuses.Rejected,
        Message = feedback
    });
    await db.SaveChangesAsync(cancellationToken);
    await AddAuditAsync(db, report.Id, "Reject", user.GetUserId(), feedback, cancellationToken);

    await eventBus.PublishAsync(new ReportRejectedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        user.GetUserId(),
        report.CreatedByUserId,
        feedback), EventRoutingKeys.ReportRejected, cancellationToken);

    return Results.Ok(ToResponse(report));
});

var deadlines = app.MapGroup("/api/deadlines")
    .WithTags("Deadlines")
    .RequireAuthorization(AuthPolicies.StudentAffairsAdministration);
deadlines.MapGet("/", async (ReportDbContext db) => Results.Ok(await db.ReportingDeadlines.OrderBy(x => x.Period).ToListAsync()));
deadlines.MapPost("/", async (DeadlineRequest request, ReportDbContext db) =>
{
    var deadline = await db.ReportingDeadlines.FirstOrDefaultAsync(x => x.Period == request.Period);
    if (deadline is null)
    {
        deadline = new ReportingDeadline { Period = request.Period.Trim() };
        db.ReportingDeadlines.Add(deadline);
    }

    deadline.DueDate = request.DueDate;
    deadline.IsActive = request.IsActive;
    await db.SaveChangesAsync();
    return Results.Ok(deadline);
});

app.MapGet("/api/deadlines/me", async (
    ReportDbContext db,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    if (!access.Any(x => x.IsManager))
    {
        return Results.Forbid();
    }

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var rows = await db.ReportingDeadlines
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.DueDate)
        .Select(x => new MyDeadlineResponse(
            x.Id,
            x.Period,
            x.DueDate,
            x.DueDate < today,
            x.DueDate.DayNumber - today.DayNumber))
        .ToListAsync(cancellationToken);
    return Results.Ok(rows);
})
.WithTags("Deadlines")
.RequireAuthorization(AuthPolicies.BusinessAccess);

using (var scope = app.Services.CreateScope())
{
    var reportDb = scope.ServiceProvider.GetRequiredService<ReportDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await reportDb.ApplyMigrationsWithRetryAsync(logger);
    await ReportSchemaUpgrader.ApplyAsync(reportDb);

    // Demo data seeding — only in Development or Docker, only when explicitly enabled
    var demoEnabled = builder.Configuration.GetValue<bool>("DemoData:Enabled");
    var environment = builder.Environment.EnvironmentName;
    if (demoEnabled && (environment == "Development" || environment == "Docker"))
    {
        var resetReports = builder.Configuration.GetValue<bool>("DemoData:ResetReports");
        var demoOptions = new DemoDataOptions();
        builder.Configuration.GetSection("DemoData").Bind(demoOptions);
        var clubIds = demoOptions.GetClubIds(); // validates and throws if misconfigured
        var resetService = new DemoResetService(reportDb);
        await DevelopmentDataSeeder.SeedAsync(reportDb, resetService, clubIds, resetReports, logger, CancellationToken.None);
    }
    else
    {
        logger.LogInformation(
            "[DevSeeder] Skipped — DemoData.Enabled={Enabled}, Environment={Env}",
            demoEnabled, environment);
    }
}

EnsureHangfireSchema(connectionString);

RecurringJob.AddOrUpdate<ReportDeadlineJobs>(
    "daily-submission-reminder",
    job => job.PublishDailyReminderAsync(CancellationToken.None),
    "0 8 * * *");
RecurringJob.AddOrUpdate<ReportDeadlineJobs>(
    "monthly-missing-report-check",
    job => job.PublishMissingReportCheckAsync(CancellationToken.None),
    "30 8 1 * *");

app.Run();

static void EnsureHangfireSchema(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return;
    }

    using var connection = new SqlConnection(connectionString);
    connection.Open();
    SqlServerObjectsInstaller.Install(connection);
}

static ReportDetail ToDetail(UpsertReportDetailRequest request, bool includeBudget = true) => new()
{
    ActivityName = request.ActivityName.Trim(),
    ActivityDate = request.ActivityDate,
    Description = request.Description.Trim(),
    ParticipantCount = Math.Max(0, request.ParticipantCount),
    Outcome = request.Outcome.Trim(),
    ActivityType = request.ActivityType?.Trim(),
    Location = request.Location?.Trim(),
    PartnerUnit = request.PartnerUnit?.Trim(),
    Objective = request.Objective?.Trim(),
    TargetParticipantCount = request.TargetParticipantCount.HasValue ? Math.Max(0, request.TargetParticipantCount.Value) : null,
    BudgetSpent = includeBudget && request.BudgetSpent.HasValue ? Math.Max(0, request.BudgetSpent.Value) : null,
    EvidenceUrl = request.EvidenceUrl?.Trim(),
    SortOrder = request.SortOrder
};

static async Task AddAuditAsync(ReportDbContext db, int reportId, string action, int actorUserId, string description, CancellationToken cancellationToken = default)
{
    db.AuditLogs.Add(new AuditLog
    {
        ReportId = reportId,
        Action = action,
        ActorUserId = actorUserId,
        Description = description
    });
    await db.SaveChangesAsync(cancellationToken);
}

static async Task<ClubAccessSnapshot?> GetAuthorAccessAsync(
    int clubId,
    string reportTag,
    string reportType,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    if (clubId <= 0)
    {
        return null;
    }

    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.FirstOrDefault(x =>
        x.ClubId == clubId
        && (x.CanManage || (x.CanManageFinance && IsFinancialReport(reportTag, reportType))));
}

static async Task<bool> CanAuthorReportsAsync(
    int clubId,
    string reportTag,
    string reportType,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    return await GetAuthorAccessAsync(
        clubId,
        reportTag,
        reportType,
        clubAccess,
        httpContext,
        cancellationToken) is not null;
}

static bool IsFinancialReport(string? tag, string? reportType)
{
    static string Normalize(string? value) => (value ?? string.Empty)
        .Trim()
        .ToUpperInvariant()
        .Replace(' ', '_');

    var normalizedTag = Normalize(tag);
    var normalizedType = Normalize(reportType);
    return normalizedTag is "FINANCE" or "FINANCIAL" or "FINANCIAL_REPORT" or "T\u00C0I_CH\u00CDNH"
        || normalizedType is "FINANCE" or "FINANCIAL" or "FINANCIAL_REPORT" or "T\u00C0I_CH\u00CDNH";
}

static bool IsFutureEventReport(string? reportType) =>
    FutureEventReportRules.IsFutureEvent(reportType);

static bool IsFutureEventReportModel(Report report) => IsFutureEventReport(report.ReportType);

static string? ValidateFutureEventDetails(
    string reportType,
    IReadOnlyCollection<UpsertReportDetailRequest> details)
{
    var vietnamToday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
    return FutureEventReportRules.Validate(
        reportType,
        details.Select(detail => new FutureEventDetailInput(
            detail.ActivityDate,
            detail.ActivityName,
            detail.Description,
            detail.Location)).ToArray(),
        vietnamToday);
}

static async Task<bool> CanManageClubAsync(
    int clubId,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Any(x => x.ClubId == clubId && x.CanManage);
}

static async Task<bool> CanReadReportAsync(
    Report report,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    if (IsReportReviewer(user) || report.CreatedByUserId == user.GetUserId())
    {
        return true;
    }

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == report.ClubId);
    return access is not null
        && (access.CanManage
            || (access.CanManageFinance && IsFutureEventReportModel(report) && report.Status != ReportStatuses.Draft)
            || (access.CanView && report.Status == ReportStatuses.Approved));
}

static async Task<bool> CanViewFinanceAsync(
    Report report,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    if (!IsFutureEventReportModel(report) || IsReportReviewer(user) || report.CreatedByUserId == user.GetUserId())
    {
        return true;
    }

    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Any(x => x.ClubId == report.ClubId && (x.CanManage || x.CanManageFinance));
}

static bool IsReportReviewer(ClaimsPrincipal user) =>
    user.IsInRole(AuthRoles.Admin)
    || user.IsInRole(AuthRoles.StudentAffairsAdmin);

static string NormalizeReportTag(string? tag, string? reportType)
{
    var value = string.IsNullOrWhiteSpace(tag) ? reportType : tag;
    return string.IsNullOrWhiteSpace(value) ? "Activity report" : value.Trim();
}

static string NormalizeReportType(string? reportType, string fallbackTag)
{
    return string.IsNullOrWhiteSpace(reportType) ? fallbackTag : reportType.Trim();
}

static ReportResponse ToResponse(Report report, bool includeFinance = true)
{
    var isUploadedFileAvailable = report.UploadedFile is not null
        && report.UploadedFile.IsActive
        && File.Exists(report.UploadedFile.StoragePath);

    return new(
        report.Id,
        report.ClubId,
        report.ClubName,
        report.Period,
        report.ReportType,
        report.Tag,
        report.Status,
        report.CreatedByUserId,
        report.DueDate,
        report.CreatedAtUtc,
        report.UpdatedAtUtc,
        report.SubmittedAtUtc,
        report.ReviewedAtUtc,
        report.Version,
        report.ExecutiveSummary,
        report.Achievements,
        report.Challenges,
        report.Recommendations,
        report.NextPeriodPlan,
        report.Details.Count,
        report.Details.Sum(x => x.ParticipantCount),
        !IsFutureEventReportModel(report) || includeFinance ? report.Details.Sum(x => x.BudgetSpent ?? 0m) : 0m,
        includeFinance ? report.BudgetProposalId : null,
        includeFinance ? report.BudgetRequestedAmount : null,
        includeFinance ? report.BudgetApprovedAmount : null,
        includeFinance ? report.BudgetDescription : null,
        includeFinance ? report.FinanceSubmittedAtUtc : null,
        report.PublishedActivityId,
        !IsFutureEventReportModel(report) || includeFinance,
        report.Details.OrderBy(x => x.SortOrder).ThenBy(x => x.ActivityDate).Select(x => new ReportDetailResponse(
            x.Id,
            x.ActivityName,
            x.ActivityDate,
            x.Description,
            x.ParticipantCount,
            x.Outcome,
            x.ActivityType,
            x.Location,
            x.PartnerUnit,
            x.Objective,
            x.TargetParticipantCount,
            !IsFutureEventReportModel(report) || includeFinance ? x.BudgetSpent : null,
            x.EvidenceUrl,
            x.SortOrder)).ToArray(),
        report.Attachments.OrderByDescending(x => x.UploadedAtUtc).Select(x => new ReportAttachmentResponse(
            x.Id,
            x.ReportDetailId,
            x.FileName,
            x.ContentType,
            x.SizeBytes,
            x.StoragePath,
            x.UploadedAtUtc)).ToArray(),
        report.Feedback.OrderByDescending(x => x.CreatedAtUtc).Select(x => new ReportFeedbackResponse(
            x.Id,
            x.ReviewerUserId,
            x.ReviewerName,
            x.Decision,
            x.Message,
            x.CreatedAtUtc)).ToArray(),
        report.ContentSource ?? ReportContentSources.StructuredForm,
        report.UploadedFile is null || !report.UploadedFile.IsActive
            ? null
            : new ReportUploadedFileResponse(
                report.UploadedFile.Id,
                report.UploadedFile.OriginalFileName,
                report.UploadedFile.ContentType,
                report.UploadedFile.FileExtension,
                report.UploadedFile.SizeBytes,
                report.UploadedFile.UploadedAtUtc,
                report.UploadedFile.UploadedByUserId,
                isUploadedFileAvailable,
                report.UploadedFile.PreviewStatus ?? "Available",
                !string.IsNullOrEmpty(report.UploadedFile.PreviewStoragePath) && File.Exists(report.UploadedFile.PreviewStoragePath),
                report.UploadedFile.PreviewErrorMessage));
}

static (bool IsValid, string ErrorMessage, string ContentType) ValidateUploadedReportFile(IFormFile file)
{
    if (file is null || file.Length == 0)
    {
        return (false, "Vui lòng chọn tệp báo cáo.", string.Empty);
    }

    if (file.Length > 20 * 1024 * 1024)
    {
        return (false, "Dung lượng tệp vượt quá giới hạn cho phép (tối đa 20 MB).", string.Empty);
    }

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".docx" or ".xlsx"))
    {
        return (false, "Định dạng tệp không được hỗ trợ. Chỉ chấp nhận các tệp .pdf, .docx, .xlsx.", string.Empty);
    }

    var contentType = ext switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    return (true, string.Empty, contentType);
}

static async Task<(string StoredFileName, string StoragePath, string Checksum, long SizeBytes, string OriginalFileName)> SaveUploadedReportFileAsync(
    IFormFile file,
    int clubId,
    IConfiguration config,
    CancellationToken cancellationToken)
{
    var rawName = Path.GetFileName(file.FileName);
    var invalidChars = Path.GetInvalidFileNameChars();
    var originalFileName = string.Concat(rawName.Where(c => !invalidChars.Contains(c))).Trim();
    if (string.IsNullOrWhiteSpace(originalFileName))
    {
        originalFileName = "report-file" + Path.GetExtension(file.FileName);
    }

    var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
    var safeExt = ext.TrimStart('.');
    var storedFileName = $"uploaded-report-{clubId}-{Guid.NewGuid():N}.{safeExt}";

    var storageDir = Path.GetFullPath(config["Uploads:StoragePath"]
        ?? Path.Combine(AppContext.BaseDirectory, "report-uploads"));
    Directory.CreateDirectory(storageDir);

    var storagePath = Path.Combine(storageDir, storedFileName);

    using var memoryStream = new MemoryStream();
    await file.CopyToAsync(memoryStream, cancellationToken);
    var bytes = memoryStream.ToArray();

    var checksumBytes = System.Security.Cryptography.SHA256.HashData(bytes);
    var checksumHex = Convert.ToHexString(checksumBytes).ToLowerInvariant();

    await File.WriteAllBytesAsync(storagePath, bytes, cancellationToken);

    return (storedFileName, storagePath, checksumHex, file.Length, originalFileName);
}
