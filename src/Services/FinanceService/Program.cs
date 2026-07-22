using System.Security.Claims;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using FinanceService.Contracts;
using FinanceService.Clients;
using FinanceService.Data;
using FinanceService.Models;
using FinanceService.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FinanceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddRedisStreamEventBus(builder.Configuration);
builder.Services.AddHttpClient<ActivityCatalogClient>(client =>
{
    var baseUrl = builder.Configuration["Services:ActivityService:BaseUrl"] ?? "http://localhost:5106";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<FutureEventReportClient>(client =>
{
    var baseUrl = builder.Configuration["Services:ReportService:BaseUrl"] ?? "http://localhost:5103";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
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

app.MapHealthChecks("/health");
app.MapGet("/error", () => Results.Problem("An unexpected error occurred.")).AllowAnonymous();
app.MapGet("/", () => Results.Ok(new { service = "Finance Service", status = "running" }));

var finance = app.MapGroup("/api/finance")
    .WithTags("Finance")
    .RequireAuthorization(AuthPolicies.BusinessAccess);

finance.MapGet("/proposals", async (
    int? clubId,
    string? status,
    int page,
    int pageSize,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    page = Math.Max(page, 1);
    pageSize = pageSize is <= 0 or > 100 ? 20 : pageSize;

    var query = db.BudgetProposals.Include(x => x.Settlements).AsQueryable();
    if (!IsFinanceReviewer(user))
    {
        var allowedClubIds = await GetFinanceClubIdsAsync(clubAccess, httpContext, cancellationToken);
        if (allowedClubIds.Count == 0)
        {
            return Results.Forbid();
        }

        if (clubId.HasValue && !allowedClubIds.Contains(clubId.Value))
        {
            return Results.Forbid();
        }

        query = query.Where(x => allowedClubIds.Contains(x.ClubId));
    }

    if (clubId.HasValue)
    {
        query = query.Where(x => x.ClubId == clubId);
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(x => x.Status == status);
    }

    var total = await query.CountAsync(cancellationToken);
    var rows = await query
        .OrderByDescending(x => x.ProposedAtUtc)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(cancellationToken);
    return Results.Ok(new { total, page, pageSize, items = rows.Select(ToBudgetProposalResponse) });
});

finance.MapGet("/proposals/{id:int}", async (
    int id,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (proposal is null)
    {
        return Results.NotFound();
    }

    if (!IsFinanceReviewer(user) && !await CanAccessFinanceClubAsync(proposal.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    return Results.Ok(ToBudgetProposalResponse(proposal));
});

finance.MapPost("/proposals", async (
    CreateBudgetProposalRequest request,
    FinanceDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    ActivityCatalogClient activityCatalog,
    FutureEventReportClient futureEventReports,
    CancellationToken cancellationToken) =>
{
    if (request.RequestedAmount <= 0)
    {
        return Results.BadRequest(new { message = "Requested amount must be greater than zero." });
    }

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == request.ClubId && x.CanManageFinance);
    if (access is null)
    {
        return Results.Forbid();
    }

    if (request.ActivityId.HasValue && request.SourceReportId.HasValue)
    {
        return Results.BadRequest(new { message = "A proposal can link either an existing activity or a future event report, not both." });
    }

    var title = request.Title?.Trim() ?? string.Empty;
    var description = request.Description?.Trim() ?? string.Empty;
    FutureEventReportSnapshot? sourceReport = null;
    if (request.SourceReportId.HasValue)
    {
        sourceReport = await futureEventReports.GetAsync(
            request.SourceReportId.Value,
            httpContext.GetBearerToken(),
            cancellationToken);
        if (sourceReport is null || sourceReport.ClubId != request.ClubId)
        {
            return Results.BadRequest(new { message = "The selected future event report does not belong to this club." });
        }

        if (!string.Equals(sourceReport.ReportType, "FUTURE_EVENT", StringComparison.OrdinalIgnoreCase)
            || sourceReport.Details.Count != 1)
        {
            return Results.BadRequest(new { message = "Only a future event report awaiting its budget can be linked." });
        }

        title = sourceReport.Details.Single().ActivityName.Trim();
        var existingProposal = await db.BudgetProposals
            .Include(x => x.Settlements)
            .FirstOrDefaultAsync(x => x.SourceReportId == sourceReport.Id, cancellationToken);
        if (existingProposal is not null)
        {
            if (existingProposal.Status == FinanceStatuses.Rejected
                && string.Equals(sourceReport.Status, "Awaiting Finance", StringComparison.OrdinalIgnoreCase))
            {
                existingProposal.Title = title;
                existingProposal.Description = description;
                existingProposal.RequestedAmount = request.RequestedAmount;
                existingProposal.ApprovedAmount = null;
                existingProposal.Status = FinanceStatuses.Submitted;
                existingProposal.ProposedByUserId = user.GetUserId();
                existingProposal.ProposedAtUtc = DateTimeOffset.UtcNow;
                existingProposal.ManagerReviewedByUserId = null;
                existingProposal.ManagerReviewedAtUtc = null;
                existingProposal.ManagerReviewNote = null;
                existingProposal.ReviewedByUserId = null;
                existingProposal.ReviewedAtUtc = null;
                existingProposal.ReviewNote = null;
                await db.SaveChangesAsync(cancellationToken);
            }

            if (sourceReport.BudgetProposalId != existingProposal.Id)
            {
                var linked = await futureEventReports.LinkBudgetAsync(
                    sourceReport.Id,
                    existingProposal.Id,
                    existingProposal.RequestedAmount,
                    existingProposal.Description,
                    httpContext.GetBearerToken(),
                    cancellationToken);
                if (!linked)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
            return Results.Ok(ToBudgetProposalResponse(existingProposal));
        }

        if (!string.Equals(sourceReport.Status, "Awaiting Finance", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { message = "This future event report is no longer awaiting a budget." });
        }
    }

    if (request.ActivityId.HasValue)
    {
        var activity = await activityCatalog.GetAsync(
            request.ActivityId.Value,
            httpContext.GetBearerToken(),
            cancellationToken);
        if (activity is null || activity.ClubId != request.ClubId)
        {
            return Results.BadRequest(new { message = "The selected activity does not belong to this club." });
        }

        if (!BudgetProposalActivityRules.CanLink(activity.MeetingDays, activity.Title, activity.Description))
        {
            return Results.BadRequest(new { message = "Weekly, monthly, or attendance activities cannot be linked to a budget proposal." });
        }

        if (string.Equals(activity.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { message = "A cancelled activity cannot be linked to a budget proposal." });
        }

        title = activity.Title.Trim();
    }

    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
    {
        return Results.BadRequest(new { message = "Proposal title and description are required." });
    }

    var proposal = new BudgetProposal
    {
        ClubId = request.ClubId,
        ClubName = access.ClubName,
        ActivityId = request.ActivityId,
        SourceReportId = request.SourceReportId,
        Title = title,
        Description = description,
        RequestedAmount = request.RequestedAmount,
        ProposedByUserId = user.GetUserId()
    };

    db.BudgetProposals.Add(proposal);
    await db.SaveChangesAsync(cancellationToken);

    if (sourceReport is not null)
    {
        var linked = await futureEventReports.LinkBudgetAsync(
            sourceReport.Id,
            proposal.Id,
            proposal.RequestedAmount,
            proposal.Description,
            httpContext.GetBearerToken(),
            cancellationToken);
        if (!linked)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
    else
    {
        await eventBus.PublishAsync(new BudgetProposalSubmittedEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            proposal.Id,
            proposal.ClubId,
            proposal.ClubName,
            proposal.RequestedAmount,
            proposal.ProposedByUserId,
            "ManagerReview",
            access.ManagerUserIds), EventRoutingKeys.BudgetProposalSubmitted, cancellationToken);
    }

    return Results.Created($"/api/finance/proposals/{proposal.Id}", ToBudgetProposalResponse(proposal));
});

finance.MapPost("/proposals/{id:int}/manager-approve", async (
    int id,
    ReviewBudgetProposalRequest request,
    FinanceDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (proposal is null) return Results.NotFound();

    if (proposal.SourceReportId.HasValue && !IsCombinedReportWorkflow(httpContext))
    {
        return Results.BadRequest(new { message = "Review this budget together with its future event report." });
    }
    if (proposal.SourceReportId.HasValue && proposal.Status == FinanceStatuses.ManagerApproved)
    {
        return Results.Ok(ToBudgetProposalResponse(proposal));
    }

    if (!BudgetProposalReviewRules.CanManagerReview(proposal.Status))
    {
        return Results.BadRequest(new { message = "Only proposals awaiting club owner review can be approved." });
    }

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == proposal.ClubId && x.IsManager);
    if (access is null) return Results.Forbid();
    if (proposal.ProposedByUserId == user.GetUserId())
    {
        return Results.BadRequest(new { message = "The proposal creator cannot approve their own proposal." });
    }

    proposal.Status = FinanceStatuses.ManagerApproved;
    proposal.ManagerReviewedByUserId = user.GetUserId();
    proposal.ManagerReviewedAtUtc = DateTimeOffset.UtcNow;
    proposal.ManagerReviewNote = string.IsNullOrWhiteSpace(request.Note)
        ? "Approved by the club owner."
        : request.Note.Trim();
    await db.SaveChangesAsync(cancellationToken);

    if (!proposal.SourceReportId.HasValue)
    {
        await eventBus.PublishAsync(new BudgetProposalSubmittedEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            proposal.Id,
            proposal.ClubId,
            proposal.ClubName,
            proposal.RequestedAmount,
            proposal.ProposedByUserId,
            "FinalReview"), EventRoutingKeys.BudgetProposalSubmitted, cancellationToken);
    }

    return Results.Ok(ToBudgetProposalResponse(proposal));
});

finance.MapPost("/proposals/{id:int}/manager-reject", async (
    int id,
    ReviewBudgetProposalRequest request,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (proposal is null) return Results.NotFound();

    if (proposal.SourceReportId.HasValue && !IsCombinedReportWorkflow(httpContext))
    {
        return Results.BadRequest(new { message = "Review this budget together with its future event report." });
    }
    if (proposal.SourceReportId.HasValue && proposal.Status == FinanceStatuses.Rejected)
    {
        return Results.Ok(ToBudgetProposalResponse(proposal));
    }

    if (!BudgetProposalReviewRules.CanManagerReview(proposal.Status))
    {
        return Results.BadRequest(new { message = "Only proposals awaiting club owner review can be rejected." });
    }

    var canManage = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .Any(x => x.ClubId == proposal.ClubId && x.IsManager);
    if (!canManage) return Results.Forbid();
    if (proposal.ProposedByUserId == user.GetUserId())
    {
        return Results.BadRequest(new { message = "The proposal creator cannot reject their own proposal." });
    }

    proposal.Status = FinanceStatuses.Rejected;
    proposal.ManagerReviewedByUserId = user.GetUserId();
    proposal.ManagerReviewedAtUtc = DateTimeOffset.UtcNow;
    proposal.ManagerReviewNote = string.IsNullOrWhiteSpace(request.Note)
        ? "Rejected by the club owner."
        : request.Note.Trim();
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToBudgetProposalResponse(proposal));
});

finance.MapPost("/proposals/{id:int}/approve", async (
    int id,
    ReviewBudgetProposalRequest request,
    FinanceDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (proposal is null)
    {
        return Results.NotFound();
    }

    if (proposal.SourceReportId.HasValue && !IsCombinedReportWorkflow(httpContext))
    {
        return Results.BadRequest(new { message = "Review this budget together with its future event report." });
    }
    if (proposal.SourceReportId.HasValue && proposal.Status == FinanceStatuses.Approved)
    {
        return Results.Ok(ToBudgetProposalResponse(proposal));
    }

    if (!BudgetProposalReviewRules.CanFinalReview(proposal.Status))
    {
        return Results.BadRequest(new { message = "The club owner must approve this proposal before final approval." });
    }

    if (proposal.ProposedByUserId == user.GetUserId())
    {
        return Results.BadRequest(new { message = "The proposal creator cannot approve their own proposal." });
    }

    var approvedAmount = request.ApprovedAmount ?? proposal.RequestedAmount;
    if (approvedAmount <= 0)
    {
        return Results.BadRequest(new { message = "Approved amount must be greater than zero." });
    }

    proposal.Status = FinanceStatuses.Approved;
    proposal.ApprovedAmount = approvedAmount;
    proposal.ReviewedByUserId = user.GetUserId();
    proposal.ReviewedAtUtc = DateTimeOffset.UtcNow;
    proposal.ReviewNote = string.IsNullOrWhiteSpace(request.Note) ? "Budget approved." : request.Note.Trim();
    db.FinanceTransactions.Add(new FinanceTransaction
    {
        ClubId = proposal.ClubId,
        Amount = approvedAmount,
        Type = TransactionTypes.BudgetApproved,
        Description = proposal.Title,
        ReferenceId = proposal.Id
    });
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new BudgetApprovedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        proposal.Id,
        proposal.ClubId,
        proposal.ClubName,
        approvedAmount,
        user.GetUserId(),
        proposal.ProposedByUserId), EventRoutingKeys.BudgetApproved, cancellationToken);

    return Results.Ok(ToBudgetProposalResponse(proposal));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

finance.MapPost("/proposals/{id:int}/reject", async (
    int id,
    ReviewBudgetProposalRequest request,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements).FirstOrDefaultAsync(x => x.Id == id);
    if (proposal is null)
    {
        return Results.NotFound();
    }

    if (proposal.SourceReportId.HasValue && !IsCombinedReportWorkflow(httpContext))
    {
        return Results.BadRequest(new { message = "Review this budget together with its future event report." });
    }
    if (proposal.SourceReportId.HasValue && proposal.Status == FinanceStatuses.Rejected)
    {
        return Results.Ok(ToBudgetProposalResponse(proposal));
    }

    if (!BudgetProposalReviewRules.CanFinalReview(proposal.Status))
    {
        return Results.BadRequest(new { message = "The club owner must review this proposal before final rejection." });
    }

    if (proposal.ProposedByUserId == user.GetUserId())
    {
        return Results.BadRequest(new { message = "The proposal creator cannot reject their own proposal." });
    }

    proposal.Status = FinanceStatuses.Rejected;
    proposal.ReviewedByUserId = user.GetUserId();
    proposal.ReviewedAtUtc = DateTimeOffset.UtcNow;
    proposal.ReviewNote = string.IsNullOrWhiteSpace(request.Note) ? "Budget rejected." : request.Note.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(ToBudgetProposalResponse(proposal));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

finance.MapGet("/settlements", async (
    string? status,
    int page,
    int pageSize,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    page = Math.Max(page, 1);
    pageSize = pageSize is <= 0 or > 100 ? 20 : pageSize;

    var query = db.Settlements.Include(x => x.BudgetProposal).AsQueryable();
    if (!IsFinanceReviewer(user))
    {
        var allowedClubIds = await GetFinanceClubIdsAsync(clubAccess, httpContext, cancellationToken);
        if (allowedClubIds.Count == 0)
        {
            return Results.Forbid();
        }

        query = query.Where(x => allowedClubIds.Contains(x.BudgetProposal.ClubId));
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(x => x.Status == status);
    }

    var total = await query.CountAsync(cancellationToken);
    var rows = await query
        .OrderByDescending(x => x.SubmittedAtUtc)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(cancellationToken);
    return Results.Ok(new { total, page, pageSize, items = rows.Select(ToSettlementResponse) });
});

finance.MapPost("/proposals/{id:int}/settlements", async (
    int id,
    CreateSettlementRequest request,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (proposal is null)
    {
        return Results.NotFound();
    }

    if (!await CanManageFinanceClubAsync(proposal.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (proposal.Status != FinanceStatuses.Approved)
    {
        return Results.BadRequest(new { message = "Only approved budget proposals can be settled." });
    }

    if (request.TotalSpent <= 0)
    {
        return Results.BadRequest(new { message = "Total spent must be greater than zero." });
    }

    // Validate receipt URL is a valid HTTPS URL
    if (!Uri.TryCreate(request.ReceiptUrl, UriKind.Absolute, out var receiptUri))
    {
        return Results.BadRequest(new { message = "Receipt URL must be a valid URL." });
    }
    if (!receiptUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Receipt URL must use HTTPS." });
    }

    if (request.TotalSpent > 1_000_000_000)
    {
        return Results.BadRequest(new { message = "Total spent exceeds maximum allowed amount." });
    }

    // Validate settlement doesn't exceed approved budget
    if (proposal.ApprovedAmount.HasValue && request.TotalSpent > proposal.ApprovedAmount.Value)
    {
        return Results.BadRequest(new { message = $"Total spent ({request.TotalSpent:N0}) exceeds approved amount ({proposal.ApprovedAmount:N0})." });
    }

    if (proposal.Settlements.Any(x => x.Status == FinanceStatuses.Submitted || x.Status == FinanceStatuses.Approved))
    {
        return Results.Conflict(new { message = "This proposal already has an active settlement." });
    }

    var settlement = new Settlement
    {
        BudgetProposalId = id,
        TotalSpent = request.TotalSpent,
        ReceiptUrl = request.ReceiptUrl.Trim()
    };
    proposal.Settlements.Add(settlement);
    db.FinanceTransactions.Add(new FinanceTransaction
    {
        ClubId = proposal.ClubId,
        Amount = request.TotalSpent,
        Type = TransactionTypes.SettlementSubmitted,
        Description = $"Settlement submitted for {proposal.Title}",
        ReferenceId = proposal.Id
    });
    await db.SaveChangesAsync();
    return Results.Ok(ToBudgetProposalResponse(proposal));
});

finance.MapPost("/settlements/{id:int}/approve", async (
    int id,
    ReviewSettlementRequest request,
    FinanceDbContext db,
    ClaimsPrincipal user) =>
{
    var settlement = await db.Settlements.Include(x => x.BudgetProposal).FirstOrDefaultAsync(x => x.Id == id);
    if (settlement is null)
    {
        return Results.NotFound();
    }

    if (settlement.Status != FinanceStatuses.Submitted)
    {
        return Results.BadRequest(new { message = "Only submitted settlements can be approved." });
    }

    if (settlement.BudgetProposal.ProposedByUserId == user.GetUserId())
    {
        return Results.BadRequest(new { message = "The proposal creator cannot approve its settlement." });
    }

    settlement.Status = FinanceStatuses.Approved;
    settlement.ReviewedByUserId = user.GetUserId();
    settlement.ReviewedAtUtc = DateTimeOffset.UtcNow;
    settlement.ReviewNote = string.IsNullOrWhiteSpace(request.Note) ? "Settlement approved." : request.Note.Trim();
    settlement.BudgetProposal.Status = FinanceStatuses.Settled;
    db.FinanceTransactions.Add(new FinanceTransaction
    {
        ClubId = settlement.BudgetProposal.ClubId,
        Amount = settlement.TotalSpent,
        Type = TransactionTypes.SettlementApproved,
        Description = $"Settlement approved for {settlement.BudgetProposal.Title}",
        ReferenceId = settlement.BudgetProposalId
    });
    await db.SaveChangesAsync();
    return Results.Ok(ToSettlementResponse(settlement));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

finance.MapGet("/transactions", async (
    int? clubId,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var query = db.FinanceTransactions.AsQueryable();
    if (!IsFinanceReviewer(user))
    {
        var allowedClubIds = await GetFinanceClubIdsAsync(clubAccess, httpContext, cancellationToken);
        if (allowedClubIds.Count == 0)
        {
            return Results.Forbid();
        }

        if (clubId.HasValue && !allowedClubIds.Contains(clubId.Value))
        {
            return Results.Forbid();
        }

        query = query.Where(x => allowedClubIds.Contains(x.ClubId));
    }

    if (clubId.HasValue)
    {
        query = query.Where(x => x.ClubId == clubId);
    }

    var rows = await query.OrderByDescending(x => x.TransactionDateUtc).Take(100).ToListAsync();
    return Results.Ok(rows.Select(ToFinanceTransactionResponse));
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.EnsureCreatedWithRetryAsync(logger);
    await FinanceSchemaUpgrader.ApplyAsync(db);
    await FinanceSeeder.SeedAsync(db);
}

app.Run();

static BudgetProposalResponse ToBudgetProposalResponse(BudgetProposal proposal) => new(
    proposal.Id,
    proposal.ClubId,
    proposal.ClubName,
    proposal.ActivityId,
    proposal.SourceReportId,
    proposal.Title,
    proposal.Description,
    proposal.RequestedAmount,
    proposal.ApprovedAmount,
    proposal.Status,
    proposal.ProposedByUserId,
    proposal.ProposedAtUtc,
    proposal.ManagerReviewedByUserId,
    proposal.ManagerReviewedAtUtc,
    proposal.ManagerReviewNote,
    proposal.ReviewedByUserId,
    proposal.ReviewedAtUtc,
    proposal.ReviewNote,
    proposal.Settlements.OrderByDescending(x => x.SubmittedAtUtc).Select(ToSettlementResponse).ToArray());

static SettlementResponse ToSettlementResponse(Settlement settlement) => new(
    settlement.Id,
    settlement.BudgetProposalId,
    settlement.TotalSpent,
    settlement.ReceiptUrl,
    settlement.Status,
    settlement.SubmittedAtUtc,
    settlement.ReviewedByUserId,
    settlement.ReviewedAtUtc,
    settlement.ReviewNote);

static FinanceTransactionResponse ToFinanceTransactionResponse(FinanceTransaction transaction) => new(
    transaction.Id,
    transaction.ClubId,
    transaction.Amount,
    transaction.Type,
    transaction.Description,
    transaction.ReferenceId,
    transaction.TransactionDateUtc);

static bool IsFinanceReviewer(ClaimsPrincipal user) =>
    user.IsInRole(AuthRoles.Admin)
    || user.IsInRole(AuthRoles.StudentAffairsAdmin);

static bool IsCombinedReportWorkflow(HttpContext httpContext) =>
    string.Equals(
        httpContext.Request.Headers["X-Combined-Report-Workflow"].ToString(),
        "true",
        StringComparison.OrdinalIgnoreCase);

static async Task<HashSet<int>> GetFinanceClubIdsAsync(
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Where(x => x.CanManageFinance || x.CanManage).Select(x => x.ClubId).ToHashSet();
}

static async Task<bool> CanAccessFinanceClubAsync(
    int clubId,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Any(x => x.ClubId == clubId && (x.CanManageFinance || x.CanManage));
}

static async Task<bool> CanManageFinanceClubAsync(
    int clubId,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Any(x => x.ClubId == clubId && x.CanManageFinance);
}
