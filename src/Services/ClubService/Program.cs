using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using ClubService.Contracts;
using ClubService.Data;
using ClubService.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ClubDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddRedisStreamEventBus(builder.Configuration);
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
app.MapGet("/", () => Results.Ok(new { service = "Club Service", status = "running" }));

var clubs = app.MapGroup("/api/clubs")
    .WithTags("Clubs")
    .RequireAuthorization(AuthPolicies.BusinessAccess);

clubs.MapGet("/", async (string? search, bool? active, ClubDbContext db, ClaimsPrincipal user) =>
{
    var query = db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .AsQueryable();
    if (!IsStudentAffairsAdministrator(user))
    {
        var currentUserId = user.GetUserId();
        query = query.Where(x =>
            x.IsActive
            || x.ManagerAssignments.Any(m => m.ManagerUserId == currentUserId && m.IsActive));
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        query = query.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
    }

    if (active.HasValue)
    {
        query = query.Where(x => x.IsActive == active);
    }

    var result = await query.OrderBy(x => x.Name).ToListAsync();
    if (IsStudentAffairsAdministrator(user))
    {
        return Results.Ok(result.Select(ToResponse));
    }

    var userId = user.GetUserId();
    var managedClubIds = result
        .Where(x => x.ManagerAssignments.Any(m => m.ManagerUserId == userId && m.IsActive))
        .Select(x => x.Id)
        .ToHashSet();
    return Results.Ok(result.Select(club => managedClubIds.Contains(club.Id) ? ToResponse(club) : ToDirectoryResponse(club)));
});

clubs.MapGet("/me/managed", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var clubsForManager = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .Where(x => x.ManagerAssignments.Any(m => m.ManagerUserId == userId && m.IsActive))
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(clubsForManager.Select(ToResponse));
});

clubs.MapGet("/me/memberships", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var memberships = await db.ClubMemberships
        .Include(x => x.Club)
        .Where(x => x.UserId == userId)
        .OrderByDescending(x => x.RequestedAtUtc)
        .ToListAsync();
    return Results.Ok(memberships.Select(ToMembershipResponse));
});

clubs.MapGet("/me/access", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var managedClubs = await db.ClubManagerAssignments
        .AsNoTracking()
        .Where(x => x.ManagerUserId == userId && x.IsActive)
        .Select(x => new { x.ClubId, x.Club.Name })
        .ToListAsync();
    var memberships = await db.ClubMemberships
        .AsNoTracking()
        .Where(x => x.UserId == userId && x.Status == ClubMembershipStatuses.Approved)
        .Select(x => new { x.ClubId, x.Club.Name, x.Role })
        .ToListAsync();

    var clubIds = managedClubs.Select(x => x.ClubId)
        .Concat(memberships.Select(x => x.ClubId))
        .Distinct()
        .OrderBy(x => x)
        .ToArray();
    var activeManagers = await db.ClubManagerAssignments
        .AsNoTracking()
        .Where(x => clubIds.Contains(x.ClubId) && x.IsActive)
        .Select(x => new { x.ClubId, x.ManagerUserId })
        .ToListAsync();
    var access = clubIds.Select(clubId =>
    {
        var managed = managedClubs.FirstOrDefault(x => x.ClubId == clubId);
        var membership = memberships.FirstOrDefault(x => x.ClubId == clubId);
        return new ClubAccessResponse(
            clubId,
            managed?.Name ?? membership?.Name ?? string.Empty,
            managed is not null,
            string.Equals(membership?.Role, ClubMemberRoles.Treasurer, StringComparison.OrdinalIgnoreCase),
            membership is not null,
            activeManagers.Where(x => x.ClubId == clubId).Select(x => x.ManagerUserId).Distinct().ToArray());
    });

    return Results.Ok(access);
});

clubs.MapGet("/applications", async (ClubDbContext db) =>
{
    var applications = await db.ClubCreationApplications
        .OrderByDescending(x => x.SubmittedAtUtc)
        .ToListAsync();
    return Results.Ok(applications.Select(ToApplicationResponse));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapGet("/applications/me", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var applications = await db.ClubCreationApplications
        .Where(x => x.RequesterUserId == userId)
        .OrderByDescending(x => x.SubmittedAtUtc)
        .ToListAsync();
    return Results.Ok(applications.Select(ToApplicationResponse));
}).RequireAuthorization(AuthPolicies.ClubMemberOnly);

clubs.MapPost("/applications", async (
    CreateClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var code = NormalizeOrGenerateClubCode(request.Code, userId);

    var validationError = ValidateClubApplication(request, code);
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    if (await db.ClubManagerAssignments.AnyAsync(x => x.ManagerUserId == userId && x.IsActive))
    {
        return Results.Conflict(new { message = "Each club owner can manage one club only." });
    }

    if (await db.Clubs.AnyAsync(x => x.Code == code))
    {
        return Results.Conflict(new { message = "Club code already exists." });
    }

    if (await db.ClubCreationApplications.AnyAsync(x => x.RequesterUserId == userId && x.Status == ClubApplicationStatuses.Submitted))
    {
        return Results.Conflict(new { message = "You already have a pending club creation application." });
    }

    var application = new ClubCreationApplication { RequesterUserId = userId };
    ApplyClubApplicationRequest(application, request, code);

    db.ClubCreationApplications.Add(application);
    await db.SaveChangesAsync();
    return Results.Created($"/api/clubs/applications/{application.Id}", ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.ClubMemberOnly);

clubs.MapPut("/applications/{applicationId:int}", async (
    int applicationId,
    CreateClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var application = await db.ClubCreationApplications.FirstOrDefaultAsync(x => x.Id == applicationId);
    if (application is null)
    {
        return Results.NotFound();
    }

    if (application.RequesterUserId != user.GetUserId())
    {
        return Results.Forbid();
    }

    if (application.Status != ClubApplicationStatuses.NeedsRevision)
    {
        return Results.Conflict(new { message = "Only applications requiring revision can be updated and resubmitted." });
    }

    var code = string.IsNullOrWhiteSpace(request.Code)
        ? application.Code
        : NormalizeOrGenerateClubCode(request.Code, application.RequesterUserId);
    var validationError = ValidateClubApplication(request, code);
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    if (await db.Clubs.AnyAsync(x => x.Code == code))
    {
        return Results.Conflict(new { message = "The club code already exists." });
    }

    ApplyClubApplicationRequest(application, request, code);
    application.Status = ClubApplicationStatuses.Submitted;
    application.ReviewNote = null;
    application.ReviewConditions = null;
    application.ReviewerSignature = null;
    application.ReviewedAtUtc = null;
    application.ReviewedByUserId = null;
    application.SubmittedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.ClubMemberOnly);

clubs.MapPost("/applications/{applicationId:int}/approve", async (
    int applicationId,
    ReviewClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db,
    IEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    if ((request.Note?.Trim().Length ?? 0) > 1000
        || (request.Conditions?.Trim().Length ?? 0) > 1000
        || (request.ReviewerSignature?.Trim().Length ?? 0) > 200)
    {
        return Results.BadRequest(new { message = "The review note cannot exceed 1,000 characters." });
    }

    var application = await db.ClubCreationApplications.FirstOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
    if (application is null)
    {
        return Results.NotFound();
    }

    if (application.Status != ClubApplicationStatuses.Submitted)
    {
        return Results.Conflict(new { message = "Application was already reviewed." });
    }

    if (await db.ClubManagerAssignments.AnyAsync(x => x.ManagerUserId == application.RequesterUserId && x.IsActive, cancellationToken))
    {
        return Results.Conflict(new { message = "Requester already owns a club." });
    }

    if (await db.Clubs.AnyAsync(x => x.Code == application.Code, cancellationToken))
    {
        return Results.Conflict(new { message = "Club code already exists." });
    }

    var club = new Club
    {
        Code = application.Code,
        Name = application.Name,
        Category = application.Category,
        Description = application.Description,
        LogoUrl = application.LogoUrl,
        ContactEmail = application.ContactEmail,
        ContactPhone = application.ContactPhone
    };
    club.ManagerAssignments.Add(new ClubManagerAssignment
    {
        ManagerUserId = application.RequesterUserId,
        ManagerName = application.RequesterName,
        IsActive = true
    });
    club.Memberships.Add(new ClubMembership
    {
        UserId = application.RequesterUserId,
        FullName = application.RequesterName,
        Email = application.ContactEmail,
        PhoneNumber = application.ContactPhone,
        Address = application.FounderOrganization,
        Role = ClubMemberRoles.Member,
        Status = ClubMembershipStatuses.Approved,
        AcceptedClubRules = true,
        CommittedToParticipate = true,
        ReviewedAtUtc = DateTimeOffset.UtcNow,
        ReviewedByUserId = user.GetUserId()
    });

    db.Clubs.Add(club);
    await db.SaveChangesAsync(cancellationToken);

    application.Status = ClubApplicationStatuses.Approved;
    application.ReviewNote = request.Note?.Trim();
    application.ReviewConditions = request.Conditions?.Trim();
    application.ReviewerSignature = request.ReviewerSignature?.Trim();
    application.CreatedClubId = club.Id;
    application.ReviewedAtUtc = DateTimeOffset.UtcNow;
    application.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new ClubCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        club.Id,
        club.Code,
        club.Name), EventRoutingKeys.ClubCreated, cancellationToken);

    return Results.Ok(ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapPost("/applications/{applicationId:int}/request-revision", async (
    int applicationId,
    ReviewClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Note))
    {
        return Results.BadRequest(new { message = "Please specify the required changes." });
    }

    if (request.Note.Trim().Length > 1000
        || (request.Conditions?.Trim().Length ?? 0) > 1000
        || (request.ReviewerSignature?.Trim().Length ?? 0) > 200)
    {
        return Results.BadRequest(new { message = "The review content exceeds the allowed length." });
    }

    var application = await db.ClubCreationApplications.FirstOrDefaultAsync(x => x.Id == applicationId);
    if (application is null)
    {
        return Results.NotFound();
    }

    if (application.Status != ClubApplicationStatuses.Submitted)
    {
        return Results.Conflict(new { message = "This application has already been processed." });
    }

    application.Status = ClubApplicationStatuses.NeedsRevision;
    application.ReviewNote = request.Note.Trim();
    application.ReviewConditions = request.Conditions?.Trim();
    application.ReviewerSignature = request.ReviewerSignature?.Trim();
    application.ReviewedAtUtc = DateTimeOffset.UtcNow;
    application.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapPost("/applications/{applicationId:int}/reject", async (
    int applicationId,
    ReviewClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Note))
    {
        return Results.BadRequest(new { message = "Please provide a rejection reason." });
    }

    if (request.Note.Trim().Length > 1000
        || (request.Conditions?.Trim().Length ?? 0) > 1000
        || (request.ReviewerSignature?.Trim().Length ?? 0) > 200)
    {
        return Results.BadRequest(new { message = "The review note cannot exceed 1,000 characters." });
    }

    var application = await db.ClubCreationApplications.FirstOrDefaultAsync(x => x.Id == applicationId);
    if (application is null)
    {
        return Results.NotFound();
    }

    if (application.Status != ClubApplicationStatuses.Submitted)
    {
        return Results.Conflict(new { message = "Application was already reviewed." });
    }

    application.Status = ClubApplicationStatuses.Rejected;
    application.ReviewNote = request.Note?.Trim();
    application.ReviewConditions = request.Conditions?.Trim();
    application.ReviewerSignature = request.ReviewerSignature?.Trim();
    application.ReviewedAtUtc = DateTimeOffset.UtcNow;
    application.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapGet("/{id:int}", async (int id, ClubDbContext db, ClaimsPrincipal user) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    var isAssignedManager = club.ManagerAssignments.Any(x => x.ManagerUserId == user.GetUserId() && x.IsActive);
    if (!club.IsActive && !IsStudentAffairsAdministrator(user) && !isAssignedManager)
    {
        return Results.NotFound();
    }

    var canViewPrivateDetails = IsStudentAffairsAdministrator(user) || isAssignedManager;
    return Results.Ok(canViewPrivateDetails ? ToResponse(club) : ToDirectoryResponse(club));
});

clubs.MapGet("/manager/{managerUserId:int}", async (int managerUserId, ClubDbContext db, ClaimsPrincipal user) =>
{
    if (!IsStudentAffairsAdministrator(user) && managerUserId != user.GetUserId())
    {
        return Results.Forbid();
    }

    var clubsForManager = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .Where(x => x.ManagerAssignments.Any(m => m.ManagerUserId == managerUserId && m.IsActive))
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(clubsForManager.Select(ToResponse));
});

clubs.MapPost("/", async (
    CreateClubRequest request,
    ClubDbContext db,
    IEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    var code = request.Code.Trim().ToUpperInvariant();
    if (await db.Clubs.AnyAsync(x => x.Code == code, cancellationToken))
    {
        return Results.Conflict(new { message = "Club code already exists." });
    }

    var club = new Club
    {
        Code = code,
        Name = request.Name.Trim(),
        Category = NormalizeClubCategory(request.Category),
        Description = request.Description.Trim(),
        LogoUrl = request.LogoUrl?.Trim(),
        ContactEmail = request.ContactEmail.Trim(),
        ContactPhone = request.ContactPhone.Trim()
    };
    db.Clubs.Add(club);
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new ClubCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        club.Id,
        club.Code,
        club.Name), EventRoutingKeys.ClubCreated, cancellationToken);

    return Results.Created($"/api/clubs/{club.Id}", ToResponse(club));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapPut("/{id:int}", async (int id, UpdateClubRequest request, ClubDbContext db) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    club.Name = request.Name.Trim();
    club.Category = NormalizeClubCategory(request.Category);
    club.Description = request.Description.Trim();
    club.LogoUrl = request.LogoUrl?.Trim();
    club.ContactEmail = request.ContactEmail.Trim();
    club.ContactPhone = request.ContactPhone.Trim();
    club.IsActive = request.IsActive;
    if (request.IsActive)
    {
        club.DeletedAtUtc = null;
        club.DeletedByUserId = null;
    }
    await db.SaveChangesAsync();
    return Results.Ok(ToResponse(club));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapDelete("/{id:int}", async (int id, ClubDbContext db, ClaimsPrincipal user) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    if (!club.IsActive)
    {
        return Results.NoContent();
    }

    club.IsActive = false;
    club.DeletedAtUtc = DateTimeOffset.UtcNow;
    club.DeletedByUserId = user.GetUserId();
    foreach (var assignment in club.ManagerAssignments.Where(x => x.IsActive))
    {
        assignment.IsActive = false;
        assignment.EndedAtUtc = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapPost("/{id:int}/managers", async (int id, AssignManagerRequest request, ClubDbContext db) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    var managesAnotherClub = await db.ClubManagerAssignments
        .AnyAsync(x => x.ManagerUserId == request.ManagerUserId && x.ClubId != id && x.IsActive);
    if (managesAnotherClub)
    {
        return Results.Conflict(new { message = "Each club owner can manage one club only." });
    }

    foreach (var assignment in club.ManagerAssignments.Where(x => x.IsActive))
    {
        assignment.IsActive = false;
        assignment.EndedAtUtc = DateTimeOffset.UtcNow;
    }

    db.ClubManagerAssignments.Add(new ClubManagerAssignment
    {
        ClubId = id,
        ManagerUserId = request.ManagerUserId,
        ManagerName = request.ManagerName.Trim(),
        IsActive = true
    });

    var existingMembership = club.Memberships.FirstOrDefault(x => x.UserId == request.ManagerUserId);
    if (existingMembership is null)
    {
        club.Memberships.Add(new ClubMembership
        {
            ClubId = id,
            UserId = request.ManagerUserId,
            FullName = request.ManagerName.Trim(),
            Role = ClubMemberRoles.Member,
            Status = ClubMembershipStatuses.Approved,
            ReviewedAtUtc = DateTimeOffset.UtcNow
        });
    }
    else
    {
        existingMembership.FullName = request.ManagerName.Trim();
        existingMembership.Status = ClubMembershipStatuses.Approved;
        existingMembership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    }

    await db.SaveChangesAsync();
    var updated = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstAsync(x => x.Id == id);
    return Results.Ok(ToResponse(updated));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

clubs.MapPost("/{id:int}/join", async (
    int id,
    JoinClubRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
    if (club is null)
    {
        return Results.NotFound();
    }

    var userId = user.GetUserId();
    if (club.ManagerAssignments.Any(x => x.ManagerUserId == userId && x.IsActive))
    {
        return Results.Conflict(new { message = "Club owner is already attached to this club." });
    }

    var validationError = ValidateJoinRequest(request, club.Category);
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var existing = club.Memberships.FirstOrDefault(x => x.UserId == userId);
    if (existing is not null)
    {
        if (existing.Status == ClubMembershipStatuses.Rejected)
        {
            existing.Status = ClubMembershipStatuses.Pending;
            existing.Role = ClubMemberRoles.Member;
            ApplyJoinRequest(existing, request);
            existing.ReviewNote = null;
            existing.RequestedAtUtc = DateTimeOffset.UtcNow;
            existing.ReviewedAtUtc = null;
            existing.ReviewedByUserId = null;
            await db.SaveChangesAsync();
            return Results.Ok(ToMembershipResponseWithClub(existing, club));
        }

        return Results.Conflict(new { message = $"Membership request already exists with status {existing.Status}." });
    }

    var membership = new ClubMembership
    {
        ClubId = id,
        UserId = userId,
        Role = ClubMemberRoles.Member,
        Status = ClubMembershipStatuses.Pending
    };
    ApplyJoinRequest(membership, request);
    db.ClubMemberships.Add(membership);
    await db.SaveChangesAsync();

    membership.Club = club;
    return Results.Created($"/api/clubs/memberships/{membership.Id}", ToMembershipResponse(membership));
}).RequireAuthorization(AuthPolicies.ClubMemberOnly);

clubs.MapGet("/{id:int}/memberships", async (int id, ClaimsPrincipal user, ClubDbContext db) =>
{
    if (!IsSuperAdmin(user) && !await UserOwnsClubAsync(db, id, user.GetUserId()))
    {
        return Results.Forbid();
    }

    var memberships = await db.ClubMemberships
        .Include(x => x.Club)
        .Where(x => x.ClubId == id)
        .OrderBy(x => x.Status)
        .ThenBy(x => x.FullName)
        .ToListAsync();
    return Results.Ok(memberships.Select(ToMembershipResponse));
});

clubs.MapPost("/memberships/{membershipId:int}/approve", async (
    int membershipId,
    ReviewClubMembershipRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (request.Note?.Trim().Length > 1000)
    {
        return Results.BadRequest(new { message = "The review note cannot exceed 1,000 characters." });
    }

    var membership = await db.ClubMemberships
        .Include(x => x.Club)
        .FirstOrDefaultAsync(x => x.Id == membershipId);
    if (membership is null)
    {
        return Results.NotFound();
    }

    if (!IsSuperAdmin(user) && !await UserOwnsClubAsync(db, membership.ClubId, user.GetUserId()))
    {
        return Results.Forbid();
    }

    if (membership.Status != ClubMembershipStatuses.Pending)
    {
        return Results.Conflict(new { message = "Only pending membership requests can be approved." });
    }

    membership.Status = ClubMembershipStatuses.Approved;
    membership.Role = ClubMemberRoles.Member;
    membership.ReviewNote = request.Note?.Trim();
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToMembershipResponse(membership));
});

clubs.MapPost("/memberships/{membershipId:int}/reject", async (
    int membershipId,
    ReviewClubMembershipRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (request.Note?.Trim().Length > 1000)
    {
        return Results.BadRequest(new { message = "The review note cannot exceed 1,000 characters." });
    }

    var membership = await db.ClubMemberships
        .Include(x => x.Club)
        .FirstOrDefaultAsync(x => x.Id == membershipId);
    if (membership is null)
    {
        return Results.NotFound();
    }

    if (!IsSuperAdmin(user) && !await UserOwnsClubAsync(db, membership.ClubId, user.GetUserId()))
    {
        return Results.Forbid();
    }

    if (membership.Status != ClubMembershipStatuses.Pending)
    {
        return Results.Conflict(new { message = "Only pending membership requests can be rejected." });
    }

    membership.Status = ClubMembershipStatuses.Rejected;
    membership.Role = ClubMemberRoles.Member;
    membership.ReviewNote = request.Note?.Trim();
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToMembershipResponse(membership));
});

clubs.MapPost("/{id:int}/treasurers", async (
    int id,
    AssignTreasurerRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (!IsSuperAdmin(user) && !await UserOwnsClubAsync(db, id, user.GetUserId()))
    {
        return Results.Forbid();
    }

    var club = await db.Clubs
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    var membership = club.Memberships.FirstOrDefault(x => x.UserId == request.MemberUserId);
    if (membership is null || membership.Status != ClubMembershipStatuses.Approved)
    {
        return Results.BadRequest(new { message = "Treasurer must be an approved member of this club." });
    }

    if (membership.Role != ClubMemberRoles.Treasurer)
    {
        var treasurerCount = club.Memberships.Count(x => x.Role == ClubMemberRoles.Treasurer && x.Status == ClubMembershipStatuses.Approved);
        if (treasurerCount >= 2)
        {
            return Results.Conflict(new { message = "A club can have at most two treasurers." });
        }
    }

    membership.FullName = string.IsNullOrWhiteSpace(request.MemberName) ? membership.FullName : request.MemberName.Trim();
    membership.Role = ClubMemberRoles.Treasurer;
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();

    membership.Club = club;
    return Results.Ok(ToMembershipResponse(membership));
});

clubs.MapPost("/memberships/{membershipId:int}/member", async (
    int membershipId,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var membership = await db.ClubMemberships
        .Include(x => x.Club)
        .FirstOrDefaultAsync(x => x.Id == membershipId);
    if (membership is null)
    {
        return Results.NotFound();
    }

    if (!IsSuperAdmin(user) && !await UserOwnsClubAsync(db, membership.ClubId, user.GetUserId()))
    {
        return Results.Forbid();
    }

    membership.Role = ClubMemberRoles.Member;
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToMembershipResponse(membership));
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.ApplyMigrationsWithRetryAsync(logger);
    await ClubSeeder.SeedAsync(db);
}

app.Run();

static ClubResponse ToResponse(Club club)
{
    var managers = club.ManagerAssignments
        .OrderByDescending(x => x.IsActive)
        .ThenByDescending(x => x.AssignedAtUtc)
        .Select(x => new ManagerAssignmentResponse(
            x.Id,
            x.ManagerUserId,
            x.ManagerName,
            x.AssignedAtUtc,
            x.EndedAtUtc,
            x.IsActive))
        .ToArray();

    var members = club.Memberships
        .OrderBy(x => x.Status)
        .ThenByDescending(x => x.Role == ClubMemberRoles.Treasurer)
        .ThenBy(x => x.FullName)
        .Select(x => ToMembershipResponseWithClub(x, club))
        .ToArray();

    return new ClubResponse(
        club.Id,
        club.Code,
        club.Name,
        club.Category,
        club.Description,
        club.LogoUrl,
        club.ContactEmail,
        club.ContactPhone,
        club.IsActive,
        managers,
        members);
}

static ClubResponse ToDirectoryResponse(Club club)
{
    var response = ToResponse(club);
    var visibleMembers = response.Members
        .Where(x => x.Status == ClubMembershipStatuses.Approved)
        .Select(x => x with
        {
            RequestMessage = null,
            DateOfBirth = null,
            Gender = string.Empty,
            Email = string.Empty,
            PhoneNumber = string.Empty,
            Address = string.Empty,
            PersonalInfo = string.Empty,
            Goals = string.Empty,
            Reason = string.Empty,
            Hobbies = string.Empty,
            Skills = string.Empty,
            Expectations = string.Empty,
            Contributions = string.Empty,
            AdditionalInfo = new Dictionary<string, string>(),
            AcceptedClubRules = false,
            CommittedToParticipate = false,
            ReviewNote = null
        })
        .ToArray();
    return response with { Members = visibleMembers };
}

static ClubMembershipResponse ToMembershipResponse(ClubMembership membership)
{
    return ToMembershipResponseWithClub(membership, membership.Club);
}

static ClubMembershipResponse ToMembershipResponseWithClub(ClubMembership membership, Club club)
{
    return new ClubMembershipResponse(
        membership.Id,
        membership.ClubId,
        club.Name,
        club.Category,
        membership.UserId,
        membership.FullName,
        membership.DateOfBirth,
        membership.Gender,
        membership.Email,
        membership.PhoneNumber,
        membership.Address,
        membership.Role,
        membership.Status,
        membership.RequestMessage,
        membership.PersonalInfo,
        membership.Goals,
        membership.Reason,
        membership.Hobbies,
        membership.Skills,
        membership.Expectations,
        membership.Contributions,
        DeserializeAdditionalInfo(membership.AdditionalInfoJson),
        membership.AcceptedClubRules,
        membership.CommittedToParticipate,
        membership.ReviewNote,
        membership.RequestedAtUtc,
        membership.ReviewedAtUtc,
        membership.ReviewedByUserId);
}

static ClubCreationApplicationResponse ToApplicationResponse(ClubCreationApplication application)
{
    return new ClubCreationApplicationResponse(
        application.Id,
        application.RequesterUserId,
        application.RequesterName,
        application.Code,
        application.Name,
        application.Category,
        application.Description,
        application.Purpose,
        application.LogoUrl,
        application.ContactEmail,
        application.ContactPhone,
        application.FounderRole,
        application.FounderOrganization,
        application.FoundingMemberCount,
        DeserializeFoundingMembers(application.FoundingMembersJson),
        application.FoundingMembersCommitted,
        application.MainActivities,
        application.ActivityFrequency,
        application.ExpectedLocation,
        application.ExpectedSchedule,
        application.MajorEvents,
        application.VenueSupport,
        application.FundingSupport,
        application.EquipmentNeeds,
        application.AdvisorNeeded,
        application.CommittedToRules,
        application.CommittedToResponsibility,
        application.CommittedToReporting,
        application.Status,
        application.ReviewNote,
        application.ReviewConditions,
        application.ReviewerSignature,
        application.CreatedClubId,
        application.SubmittedAtUtc,
        application.ReviewedAtUtc,
        application.ReviewedByUserId);
}

static bool IsStudentAffairsAdministrator(ClaimsPrincipal user)
{
    return user.IsInRole(AuthRoles.Admin)
        || user.IsInRole(AuthRoles.StudentAffairsAdmin);
}

static bool IsSuperAdmin(ClaimsPrincipal user) => user.IsInRole(AuthRoles.Admin);

static Task<bool> UserOwnsClubAsync(ClubDbContext db, int clubId, int userId)
{
    return db.ClubManagerAssignments.AnyAsync(x => x.ClubId == clubId && x.ManagerUserId == userId && x.IsActive);
}

static string? ValidateClubApplication(CreateClubApplicationRequest request, string normalizedCode)
{
    if (string.IsNullOrWhiteSpace(normalizedCode)
        || string.IsNullOrWhiteSpace(request.Name)
        || string.IsNullOrWhiteSpace(request.Description)
        || string.IsNullOrWhiteSpace(request.Purpose)
        || string.IsNullOrWhiteSpace(request.Category)
        || string.IsNullOrWhiteSpace(request.FounderFullName)
        || string.IsNullOrWhiteSpace(request.FounderRole)
        || string.IsNullOrWhiteSpace(request.FounderEmail)
        || string.IsNullOrWhiteSpace(request.FounderPhone)
        || string.IsNullOrWhiteSpace(request.FounderOrganization)
        || string.IsNullOrWhiteSpace(request.MainActivities)
        || string.IsNullOrWhiteSpace(request.ActivityFrequency))
    {
        return "Please provide the club code, name, description, purpose, reason, and contact information.";
    }

    if (!Regex.IsMatch(normalizedCode, "^[A-Z0-9_-]{2,20}$"))
    {
        return "The club code must contain 2-20 letters, numbers, hyphens, or underscores.";
    }

    if (!MailAddress.TryCreate(request.FounderEmail.Trim(), out var email)
        || !string.Equals(email.Address, request.FounderEmail.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        return "The contact email is invalid.";
    }

    if (!Regex.IsMatch(request.FounderPhone.Trim(), "^\\+?[0-9]{9,15}$"))
    {
        return "The phone number must contain 9 to 15 digits.";
    }

    if (request.Name.Trim().Length > 150
        || request.Description.Trim().Length > 1000
        || request.Purpose.Trim().Length > 1000
        || request.MainActivities.Trim().Length > 2000)
    {
        return "The application content exceeds the allowed length.";
    }

    if (!ClubCategories.All.Contains(request.Category.Trim().ToUpperInvariant()))
    {
        return "The club category is invalid.";
    }

    if (!ClubResourceOptions.All.Contains(request.VenueSupport?.Trim().ToUpperInvariant())
        || !ClubFundingOptions.All.Contains(request.FundingSupport?.Trim().ToUpperInvariant()))
    {
        return "The venue or funding option is invalid.";
    }

    if (request.FoundingMembers is null
        || request.FoundingMemberCount < 1
        || request.FoundingMemberCount < request.FoundingMembers.Count)
    {
        return "The number of founding members must be valid and cannot be less than the submitted member list.";
    }

    if (request.FoundingMembers.Any(member =>
        string.IsNullOrWhiteSpace(member.FullName)
        || string.IsNullOrWhiteSpace(member.Organization)
        || !MailAddress.TryCreate(member.Email?.Trim(), out _)))
    {
        return "The founding member list contains invalid information.";
    }

    if (!request.FoundingMembersCommitted
        || !request.CommittedToRules
        || !request.CommittedToResponsibility
        || !request.CommittedToReporting)
    {
        return "You must accept all commitments before submitting the application.";
    }

    return null;
}

static string? ValidateJoinRequest(JoinClubRequest request, string clubCategory)
{
    if (string.IsNullOrWhiteSpace(request.FullName)
        || request.DateOfBirth is null
        || string.IsNullOrWhiteSpace(request.Gender)
        || string.IsNullOrWhiteSpace(request.Email)
        || string.IsNullOrWhiteSpace(request.PhoneNumber)
        || string.IsNullOrWhiteSpace(request.Reason))
    {
        return "Please provide your personal information, objectives, and reason for joining the club.";
    }

    if ((request.Message?.Trim().Length ?? 0) > 1000
        || request.FullName.Trim().Length > 200
        || request.Reason.Trim().Length > 1000
        || (request.Hobbies?.Trim().Length ?? 0) > 1000
        || (request.Skills?.Trim().Length ?? 0) > 1000
        || (request.Expectations?.Trim().Length ?? 0) > 1000
        || (request.Contributions?.Trim().Length ?? 0) > 1000)
    {
        return "The membership application exceeds the allowed length.";
    }

    var dateOfBirth = request.DateOfBirth.Value;
    if (dateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow)
        || dateOfBirth < DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-100)))
    {
        return "The date of birth is invalid.";
    }

    if (!ClubGenders.All.Contains(request.Gender.Trim().ToUpperInvariant()))
    {
        return "The gender value is invalid.";
    }

    if (!MailAddress.TryCreate(request.Email.Trim(), out _))
    {
        return "The email address is invalid.";
    }

    if (!Regex.IsMatch(request.PhoneNumber.Trim(), "^\\+?[0-9]{9,15}$"))
    {
        return "The phone number must contain 9 to 15 digits.";
    }

    if (!request.AcceptedClubRules || !request.CommittedToParticipate)
    {
        return "You must accept both club participation commitments.";
    }

    var allowedAdditionalFields = GetAdditionalFieldKeys(clubCategory);
    if (request.AdditionalInfo is { Count: > 0 }
        && request.AdditionalInfo.Any(item =>
            !allowedAdditionalFields.Contains(item.Key)
            || item.Value?.Trim().Length > 1000))
    {
        return "The additional information does not match the club category.";
    }

    return null;
}

static string NormalizeClubCategory(string? category)
{
    var normalized = category?.Trim().ToUpperInvariant();
    return ClubCategories.All.Contains(normalized) ? normalized! : ClubCategories.Other;
}

static string NormalizeOrGenerateClubCode(string? code, int requesterUserId)
{
    if (!string.IsNullOrWhiteSpace(code))
    {
        return code.Trim().ToUpperInvariant();
    }

    var suffix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000;
    return $"CLB-{requesterUserId}-{suffix}";
}

static void ApplyClubApplicationRequest(
    ClubCreationApplication application,
    CreateClubApplicationRequest request,
    string code)
{
    application.RequesterName = request.FounderFullName.Trim();
    application.Code = code;
    application.Name = request.Name.Trim();
    application.Category = NormalizeClubCategory(request.Category);
    application.Description = request.Description.Trim();
    application.Purpose = request.Purpose.Trim();
    application.Reason = request.Purpose.Trim();
    application.LogoUrl = string.IsNullOrWhiteSpace(request.LogoUrl) ? null : request.LogoUrl.Trim();
    application.ContactEmail = request.FounderEmail.Trim();
    application.ContactPhone = request.FounderPhone.Trim();
    application.FounderRole = request.FounderRole.Trim();
    application.FounderOrganization = request.FounderOrganization.Trim();
    application.FoundingMemberCount = request.FoundingMemberCount;
    application.FoundingMembersJson = JsonSerializer.Serialize(request.FoundingMembers ?? []);
    application.FoundingMembersCommitted = request.FoundingMembersCommitted;
    application.MainActivities = request.MainActivities.Trim();
    application.ActivityFrequency = request.ActivityFrequency.Trim();
    application.ExpectedLocation = request.ExpectedLocation?.Trim() ?? string.Empty;
    application.ExpectedSchedule = request.ExpectedSchedule?.Trim() ?? string.Empty;
    application.MajorEvents = request.MajorEvents?.Trim() ?? string.Empty;
    application.VenueSupport = request.VenueSupport.Trim().ToUpperInvariant();
    application.FundingSupport = request.FundingSupport.Trim().ToUpperInvariant();
    application.EquipmentNeeds = request.EquipmentNeeds?.Trim() ?? string.Empty;
    application.AdvisorNeeded = request.AdvisorNeeded;
    application.CommittedToRules = request.CommittedToRules;
    application.CommittedToResponsibility = request.CommittedToResponsibility;
    application.CommittedToReporting = request.CommittedToReporting;
}

static void ApplyJoinRequest(ClubMembership membership, JoinClubRequest request)
{
    membership.FullName = request.FullName.Trim();
    membership.DateOfBirth = request.DateOfBirth;
    membership.Gender = request.Gender.Trim().ToUpperInvariant();
    membership.Email = request.Email.Trim();
    membership.PhoneNumber = request.PhoneNumber.Trim();
    membership.Address = request.Address?.Trim() ?? string.Empty;
    membership.RequestMessage = request.Message?.Trim();
    membership.PersonalInfo = $"{membership.Email} | {membership.PhoneNumber}";
    membership.Goals = request.Expectations?.Trim() ?? string.Empty;
    membership.Reason = request.Reason.Trim();
    membership.Hobbies = request.Hobbies?.Trim() ?? string.Empty;
    membership.Skills = request.Skills?.Trim() ?? string.Empty;
    membership.Expectations = request.Expectations?.Trim() ?? string.Empty;
    membership.Contributions = request.Contributions?.Trim() ?? string.Empty;
    membership.AdditionalInfoJson = JsonSerializer.Serialize(request.AdditionalInfo ?? new Dictionary<string, string>());
    membership.AcceptedClubRules = request.AcceptedClubRules;
    membership.CommittedToParticipate = request.CommittedToParticipate;
}

static IReadOnlyDictionary<string, string> DeserializeAdditionalInfo(string json)
{
    try
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    }
    catch (JsonException)
    {
        return new Dictionary<string, string>();
    }
}

static IReadOnlyCollection<FoundingMemberResponse> DeserializeFoundingMembers(string json)
{
    try
    {
        var members = JsonSerializer.Deserialize<List<FoundingMemberRequest>>(json) ?? [];
        return members
            .Select(member => new FoundingMemberResponse(member.FullName, member.Organization, member.Email))
            .ToArray();
    }
    catch (JsonException)
    {
        return [];
    }
}

static HashSet<string> GetAdditionalFieldKeys(string category)
{
    return NormalizeClubCategory(category) switch
    {
        ClubCategories.Sports => ["sport", "level", "experience"],
        ClubCategories.Arts => ["artField", "level"],
        ClubCategories.Academic => ["academicInterest", "learningGoal"],
        ClubCategories.Volunteer => ["volunteerInterest", "socialWorkExperience"],
        ClubCategories.Technology => ["programmingLanguages", "projects"],
        _ => ["other"]
    };
}
