using System.Security.Claims;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportService.Attachments;
using ReportService.Contracts;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Models;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ReportDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.Configure<ReportAttachmentOptions>(builder.Configuration.GetSection(ReportAttachmentOptions.SectionName));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddRabbitMqEventBus(builder.Configuration);
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

var reports = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization(AuthPolicies.AdminOrClubManagerOrMember);

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
        .Include(x => x.Details)
        .Include(x => x.Attachments)
        .Include(x => x.Feedback)
        .AsQueryable();

    if (!IsAdministrator(user))
    {
        var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
        var managedClubIds = access.Where(x => x.CanManage).Select(x => x.ClubId).ToHashSet();
        var userId = user.GetUserId();
        query = query.Where(x => managedClubIds.Contains(x.ClubId) || x.CreatedByUserId == userId);
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

    return Results.Ok(new { total, page, pageSize, items = rows.Select(ToResponse) });
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
    if (!IsAdministrator(user))
    {
        var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
        var managedClubIds = access.Where(x => x.CanManage).Select(x => x.ClubId).ToHashSet();
        var userId = user.GetUserId();
        query = query.Where(x => managedClubIds.Contains(x.ClubId) || x.CreatedByUserId == userId);
    }

    var allReports = await query.ToListAsync(cancellationToken);
    return Results.Ok(new ReportSummaryResponse(
        allReports.Count,
        allReports.Count(x => x.Status == ReportStatuses.Draft),
        allReports.Count(x => x.Status == ReportStatuses.Submitted),
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
});

var kpis = app.MapGroup("/api/kpis").WithTags("KPI").RequireAuthorization(AuthPolicies.AdminOrClubManagerOrMember);
kpis.MapGet("/rules", () => Results.Ok(new[]
{
    new KpiRuleResponse("APPROVED_REPORT", "Approved report", 50, "Each approved period report adds KPI points."),
    new KpiRuleResponse("ACTIVITY", "Reported activity", 5, "Each approved activity detail contributes operational KPI."),
    new KpiRuleResponse("PARTICIPATION", "Participant engagement", 0.1m, "Each participant in approved activities contributes 0.1 point."),
    new KpiRuleResponse("REJECTED_REPORT", "Rejected report penalty", -10, "Rejected reports reduce KPI until revised and approved."),
    new KpiRuleResponse("OVERDUE_REPORT", "Overdue report penalty", -20, "Draft or rejected reports past due date reduce KPI.")
}));

kpis.MapGet("/leaderboard", async (string? period, ReportDbContext db) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var query = db.Reports.Include(x => x.Details).AsQueryable();
    if (!string.IsNullOrWhiteSpace(period))
    {
        query = query.Where(x => x.Period == period);
    }

    var reportsForKpi = await query.ToListAsync();
    var ranked = reportsForKpi
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
        .Select((row, index) => new KpiLeaderboardRow(
            index + 1,
            row.ClubId,
            row.ClubName,
            row.Points,
            row.ApprovedReports,
            row.Activities,
            row.Participants,
            row.RejectedReports,
            row.OverdueReports))
        .ToArray();

    return Results.Ok(new KpiLeaderboardResponse(period, DateTimeOffset.UtcNow, ranked));
});

reports.MapGet("/{id:int}", async (
    int id,
    ReportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
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

    if (!await CanReadReportAsync(report, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    return Results.Ok(ToResponse(report));
});

reports.MapPost("/", async (
    CreateReportRequest request,
    ReportDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var authorAccess = await GetAuthorAccessAsync(request.ClubId, clubAccess, httpContext, cancellationToken);
    if (authorAccess is null)
    {
        return Results.Forbid();
    }

    var tag = NormalizeReportTag(request.Tag, request.ReportType);
    var reportType = NormalizeReportType(request.ReportType, tag);
    var period = request.Period.Trim();

    if (await db.Reports.AnyAsync(x => x.ClubId == request.ClubId && x.Period == period && x.Tag == tag))
    {
        return Results.Conflict(new { message = "A report already exists for this club, period, and tag." });
    }

    var report = new Report
    {
        ClubId = request.ClubId,
        ClubName = authorAccess.ClubName,
        Period = period,
        ReportType = reportType,
        Tag = tag,
        DueDate = request.DueDate,
        CreatedByUserId = user.GetUserId(),
        Details = request.Details.Select(ToDetail).ToList()
    };

    db.Reports.Add(report);
    await db.SaveChangesAsync();
    await AddAuditAsync(db, report.Id, "Create", user.GetUserId(), "Report draft created.");
    return Results.Created($"/api/reports/{report.Id}", ToResponse(report));
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

    if (report.CreatedByUserId != user.GetUserId()
        || !await CanAuthorReportsAsync(report.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Only draft or rejected reports can be edited." });
    }

    var period = request.Period.Trim();
    var tag = NormalizeReportTag(request.Tag, request.ReportType);
    if (await db.Reports.AnyAsync(x => x.Id != id && x.ClubId == report.ClubId && x.Period == period && x.Tag == tag))
    {
        return Results.Conflict(new { message = "Another report already uses this club, period, and tag." });
    }

    report.Period = period;
    report.Tag = tag;
    report.ReportType = NormalizeReportType(request.ReportType, tag);
    report.DueDate = request.DueDate;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    report.Version++;
    db.ReportDetails.RemoveRange(report.Details);
    report.Details = request.Details.Select(ToDetail).ToList();
    await db.SaveChangesAsync();
    await AddAuditAsync(db, report.Id, "Update", user.GetUserId(), "Report draft updated.");
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
        || !await CanAuthorReportsAsync(report.ClubId, clubAccess, httpContext, cancellationToken))
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
        || !await CanAuthorReportsAsync(report.ClubId, clubAccess, httpContext, cancellationToken))
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
    var report = await db.Reports.Include(x => x.Details).Include(x => x.Attachments).Include(x => x.Feedback).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (report is null)
    {
        return Results.NotFound();
    }

    var authorAccess = await GetAuthorAccessAsync(report.ClubId, clubAccess, httpContext, cancellationToken);
    if (authorAccess is null || report.CreatedByUserId != user.GetUserId())
    {
        return Results.Forbid();
    }

    if (report.Details.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one activity detail is required before submission." });
    }

    if (report.Status is not (ReportStatuses.Draft or ReportStatuses.Rejected))
    {
        return Results.BadRequest(new { message = "Only draft or rejected reports can be submitted." });
    }

    report.Status = authorAccess.CanManage ? ReportStatuses.UnderReview : ReportStatuses.Submitted;
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
        report.Status == ReportStatuses.Submitted
            ? authorAccess.ManagerUserIds.Cast<int?>().FirstOrDefault()
            : null), EventRoutingKeys.ReportSubmitted, cancellationToken);

    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/review", async (
    int id,
    ReportDbContext db,
    IEventBus eventBus,
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

    // Creator cannot review their own report
    if (report.CreatedByUserId == user.GetUserId())
    {
        return Results.Forbid();
    }

    // Must have club management permission
    if (!await CanManageClubAsync(report.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
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
        null), EventRoutingKeys.ReportSubmitted, cancellationToken);
    return Results.Ok(ToResponse(report));
});

reports.MapPost("/{id:int}/approve", async (int id, ReviewRequest request, ReportDbContext db, IEventBus eventBus, ClaimsPrincipal user, CancellationToken cancellationToken) =>
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

    report.Status = ReportStatuses.Approved;
    report.ReviewedByUserId = user.GetUserId();
    report.ReviewedAtUtc = DateTimeOffset.UtcNow;
    report.UpdatedAtUtc = DateTimeOffset.UtcNow;
    report.Feedback.Add(new ReportFeedback
    {
        ReviewerUserId = user.GetUserId(),
        ReviewerName = user.GetDisplayName(),
        Decision = ReportStatuses.Approved,
        Message = string.IsNullOrWhiteSpace(request.Feedback) ? "Báo cáo đã được phê duyệt." : request.Feedback.Trim()
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
}).RequireAuthorization(AuthPolicies.AdminOnly);

reports.MapPost("/{id:int}/reject", async (
    int id,
    ReviewRequest request,
    ReportDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var feedback = string.IsNullOrWhiteSpace(request.Feedback) ? "Vui lòng bổ sung nội dung và gửi lại báo cáo." : request.Feedback.Trim();
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
        : IsAdministrator(user);
    if (!canReject)
    {
        return Results.Forbid();
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

var deadlines = app.MapGroup("/api/deadlines").WithTags("Deadlines").RequireAuthorization(AuthPolicies.AdminOnly);
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ReportDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.ApplyMigrationsWithRetryAsync(logger);
    await ReportSeeder.SeedAsync(db);
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

static ReportDetail ToDetail(UpsertReportDetailRequest request) => new()
{
    ActivityName = request.ActivityName.Trim(),
    ActivityDate = request.ActivityDate,
    Description = request.Description.Trim(),
    ParticipantCount = Math.Max(0, request.ParticipantCount),
    Outcome = request.Outcome.Trim()
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
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    if (clubId <= 0)
    {
        return null;
    }

    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.FirstOrDefault(x => x.ClubId == clubId && x.CanManageFinance);
}

static async Task<bool> CanAuthorReportsAsync(
    int clubId,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    return await GetAuthorAccessAsync(clubId, clubAccess, httpContext, cancellationToken) is not null;
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
    if (IsAdministrator(user) || report.CreatedByUserId == user.GetUserId())
    {
        return true;
    }

    return await CanManageClubAsync(report.ClubId, clubAccess, httpContext, cancellationToken);
}

static bool IsAdministrator(ClaimsPrincipal user) =>
    user.IsInRole(AuthRoles.Admin)
    || user.IsInRole(AuthRoles.SystemAdmin)
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

static ReportResponse ToResponse(Report report) => new(
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
    report.Details.OrderBy(x => x.ActivityDate).Select(x => new ReportDetailResponse(
        x.Id,
        x.ActivityName,
        x.ActivityDate,
        x.Description,
        x.ParticipantCount,
        x.Outcome)).ToArray(),
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
        x.CreatedAtUtc)).ToArray());
