using System.Security.Claims;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using FinanceService.Contracts;
using FinanceService.Data;
using FinanceService.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FinanceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddRabbitMqEventBus(builder.Configuration);
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

var finance = app.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization(AuthPolicies.AdminOrClubManagerOrMember);

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
    if (!IsAdministrator(user))
    {
        var allowedClubIds = await GetFinanceClubIdsAsync(clubAccess, httpContext, cancellationToken);
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

    if (!IsAdministrator(user) && !await CanManageFinanceClubAsync(proposal.ClubId, clubAccess, httpContext, cancellationToken))
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

    var proposal = new BudgetProposal
    {
        ClubId = request.ClubId,
        ClubName = access.ClubName,
        ActivityId = request.ActivityId,
        Title = request.Title.Trim(),
        Description = request.Description.Trim(),
        RequestedAmount = request.RequestedAmount,
        ProposedByUserId = user.GetUserId()
    };

    db.BudgetProposals.Add(proposal);
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new BudgetProposalSubmittedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        proposal.Id,
        proposal.ClubId,
        proposal.ClubName,
        proposal.RequestedAmount,
        proposal.ProposedByUserId), EventRoutingKeys.BudgetProposalSubmitted, cancellationToken);

    return Results.Created($"/api/finance/proposals/{proposal.Id}", ToBudgetProposalResponse(proposal));
});

finance.MapPost("/proposals/{id:int}/approve", async (
    int id,
    ReviewBudgetProposalRequest request,
    FinanceDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    CancellationToken cancellationToken) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (proposal is null)
    {
        return Results.NotFound();
    }

    if (proposal.Status != FinanceStatuses.Submitted)
    {
        return Results.BadRequest(new { message = "Only submitted budget proposals can be approved." });
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
}).RequireAuthorization(AuthPolicies.AdminOnly);

finance.MapPost("/proposals/{id:int}/reject", async (
    int id,
    ReviewBudgetProposalRequest request,
    FinanceDbContext db,
    ClaimsPrincipal user) =>
{
    var proposal = await db.BudgetProposals.Include(x => x.Settlements).FirstOrDefaultAsync(x => x.Id == id);
    if (proposal is null)
    {
        return Results.NotFound();
    }

    if (proposal.Status != FinanceStatuses.Submitted)
    {
        return Results.BadRequest(new { message = "Only submitted budget proposals can be rejected." });
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
}).RequireAuthorization(AuthPolicies.AdminOnly);

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
    if (!IsAdministrator(user))
    {
        var allowedClubIds = await GetFinanceClubIdsAsync(clubAccess, httpContext, cancellationToken);
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
}).RequireAuthorization(AuthPolicies.AdminOnly);

finance.MapGet("/transactions", async (
    int? clubId,
    FinanceDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var query = db.FinanceTransactions.AsQueryable();
    if (!IsAdministrator(user))
    {
        var allowedClubIds = await GetFinanceClubIdsAsync(clubAccess, httpContext, cancellationToken);
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
    await FinanceSeeder.SeedAsync(db);
}

app.Run();

static BudgetProposalResponse ToBudgetProposalResponse(BudgetProposal proposal) => new(
    proposal.Id,
    proposal.ClubId,
    proposal.ClubName,
    proposal.ActivityId,
    proposal.Title,
    proposal.Description,
    proposal.RequestedAmount,
    proposal.ApprovedAmount,
    proposal.Status,
    proposal.ProposedByUserId,
    proposal.ProposedAtUtc,
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

static bool IsAdministrator(ClaimsPrincipal user) =>
    user.IsInRole(AuthRoles.Admin)
    || user.IsInRole(AuthRoles.SystemAdmin)
    || user.IsInRole(AuthRoles.StudentAffairsAdmin);

static async Task<HashSet<int>> GetFinanceClubIdsAsync(
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Where(x => x.CanManageFinance).Select(x => x.ClubId).ToHashSet();
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
