using System.Security.Claims;
using System.Threading.RateLimiting;
using AuthService.Contracts;
using AuthService.Data;
using AuthService.Models;
using AuthService.Services;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddClubReportJwt(builder.Configuration);

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict rate limit for login attempts - prevent brute force
    options.AddPolicy("loginLimit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Moderate limit for registration
    options.AddPolicy("registerLimit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(5),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Refresh token limit - per user to prevent token stealing abuse
    options.AddPolicy("refreshLimit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/error", () => Results.Problem("An unexpected error occurred.")).AllowAnonymous();
app.MapGet("/", () => Results.Ok(new { service = "Auth Service", status = "running" }));

var auth = app.MapGroup("/api/auth").WithTags("Authentication");
auth.MapPost("/login", async (
    LoginRequest request,
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    RefreshTokenService refreshTokenService) =>
{
    var user = await db.Users
        .Include(x => x.UserRoles)
        .ThenInclude(x => x.Role)
        .FirstOrDefaultAsync(x => x.Username == request.Username || x.Email == request.Username);

    if (user is null || !user.IsActive || user.IsLocked)
    {
        return Results.Unauthorized();
    }

    if (!HasValidActorConfiguration(user))
    {
        return Results.Forbid();
    }

    var passwordResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
    if (passwordResult == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    var refreshToken = await refreshTokenService.CreateRefreshTokenAsync(user.Id);
    return Results.Ok(refreshTokenService.CreateAuthResponse(user, refreshToken));
}).AllowAnonymous().RequireRateLimiting("loginLimit");

auth.MapPost("/register", async (
    RegisterRequest request,
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    RefreshTokenService refreshTokenService) =>
{
    if (string.IsNullOrWhiteSpace(request.Username)
        || string.IsNullOrWhiteSpace(request.Email)
        || string.IsNullOrWhiteSpace(request.FullName)
        || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "Username, full name, email and password are required." });
    }

    var username = request.Username.Trim();
    var email = request.Email.Trim();
    var fullName = request.FullName.Trim();

    if (username.Length > 100 || fullName.Length > 200 || email.Length > 200)
    {
        return Results.BadRequest(new { message = "Username, full name or email exceeds the allowed length." });
    }

    if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsedEmail)
        || !string.Equals(parsedEmail.Address, email, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Email address is invalid." });
    }

    // Password complexity validation
    var password = request.Password;
    if (password.Length < 8)
    {
        return Results.BadRequest(new { message = "Password must be at least 8 characters." });
    }
    var hasUpper = password.Any(char.IsUpper);
    var hasLower = password.Any(char.IsLower);
    var hasDigit = password.Any(char.IsDigit);
    var hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));
    if (!(hasUpper && hasLower && hasDigit && hasSpecial))
    {
        return Results.BadRequest(new { message = "Password must contain uppercase, lowercase, digit, and special character." });
    }

    if (await db.Users.AnyAsync(x => x.Username == username || x.Email == email))
    {
        return Results.Conflict(new { message = "Username or email already exists." });
    }

    var memberRole = await db.Roles.FirstOrDefaultAsync(x => x.Name == AuthRoles.ClubMember);
    if (memberRole is null)
    {
        memberRole = new Role { Name = AuthRoles.ClubMember };
        db.Roles.Add(memberRole);
        await db.SaveChangesAsync();
    }

    var user = new User
    {
        Username = username,
        FullName = fullName,
        Email = email,
        IsActive = true
    };
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    user.UserRoles.Add(new UserRole { User = user, Role = memberRole });

    db.Users.Add(user);
    await db.SaveChangesAsync();

    var refreshToken = await refreshTokenService.CreateRefreshTokenAsync(user.Id);
    return Results.Created($"/api/users/{user.Id}", refreshTokenService.CreateAuthResponse(user, refreshToken, [AuthRoles.ClubMember]));
}).AllowAnonymous().RequireRateLimiting("registerLimit");

auth.MapPost("/refresh", async (
    RefreshTokenRequest request,
    RefreshTokenService refreshTokenService,
    AuthDbContext db) =>
{
    var oldToken = await refreshTokenService.GetRefreshTokenAsync(request.RefreshToken);
    if (oldToken is null)
    {
        return Results.Unauthorized();
    }

    if (!oldToken.IsActive)
    {
        // Token is expired or revoked - revoke entire family for security
        if (oldToken.IsRevoked && !oldToken.IsExpired)
        {
            await refreshTokenService.RevokeFamilyAsync(oldToken.FamilyId, null);
        }
        return Results.Unauthorized();
    }

    if (!HasValidActorConfiguration(oldToken.User))
    {
        return Results.Forbid();
    }

    var clientIp = "unknown"; // In production, get from HttpContext
    var rotatedToken = await refreshTokenService.RotateRefreshTokenAsync(oldToken, clientIp);
    if (rotatedToken is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(refreshTokenService.CreateAuthResponse(oldToken.User, rotatedToken));
}).AllowAnonymous().RequireRateLimiting("refreshLimit");

auth.MapPost("/logout", async (
    RefreshTokenRequest request,
    RefreshTokenService refreshTokenService,
    AuthDbContext db) =>
{
    var token = await refreshTokenService.GetRefreshTokenAsync(request.RefreshToken);
    if (token is not null)
    {
        await refreshTokenService.RevokeFamilyAsync(token.FamilyId, null);
    }
    return Results.NoContent();
}).RequireAuthorization();

var users = app.MapGroup("/api/users")
    .WithTags("Users")
    .RequireAuthorization(AuthPolicies.SystemAdministration);
users.MapGet("/", async (
    string? search,
    int? page,
    int? pageSize,
    AuthDbContext db,
    CancellationToken cancellationToken) =>
{
    var resolvedPage = Math.Max(page ?? 1, 1);
    var resolvedPageSize = pageSize is null or <= 0 or > 100 ? 20 : pageSize.Value;

    var query = db.Users.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        query = query.Where(x =>
            x.Username.Contains(term)
            || x.FullName.Contains(term)
            || x.Email.Contains(term)
            || x.UserRoles.Any(userRole => userRole.Role.Name.Contains(term)));
    }

    var total = await query.CountAsync(cancellationToken);
    var usersResult = await query
        .Include(x => x.UserRoles)
        .ThenInclude(x => x.Role)
        .OrderBy(x => x.FullName)
        .ThenBy(x => x.Id)
        .Skip((resolvedPage - 1) * resolvedPageSize)
        .Take(resolvedPageSize)
        .ToListAsync(cancellationToken);
    return Results.Ok(new
    {
        items = usersResult.Select(x => ToSummary(x)),
        total,
        page = resolvedPage,
        pageSize = resolvedPageSize
    });
});

users.MapPost("/", async (
    CreateUserRequest request,
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    ClaimsPrincipal actor) =>
{
    if (await db.Users.AnyAsync(x => x.Username == request.Username || x.Email == request.Email))
    {
        return Results.Conflict(new { message = "Username or email already exists." });
    }

    var requestedRoleNames = request.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (requestedRoleNames.Length != 1 || !AuthRoles.IsKnown(requestedRoleNames[0]))
    {
        return Results.BadRequest(new { message = "Each account must have exactly one predefined actor role." });
    }

    if (requestedRoleNames[0] == AuthRoles.Admin && !actor.IsInRole(AuthRoles.Admin))
    {
        return Results.Forbid();
    }

    var roles = await db.Roles.Where(x => requestedRoleNames.Contains(x.Name)).ToListAsync();
    if (roles.Count != 1)
    {
        return Results.BadRequest(new { message = "The requested actor role is not available." });
    }

    var user = new User
    {
        Username = request.Username,
        FullName = request.FullName,
        Email = request.Email,
        IsActive = true
    };
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    db.Users.Add(user);
    await db.SaveChangesAsync();

    db.UserRoles.AddRange(roles.Select(role => new UserRole { UserId = user.Id, RoleId = role.Id }));
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/{user.Id}", ToSummary(user, withRoles: roles.Select(x => x.Name)));
});

users.MapPut("/{id:int}", async (
    int id,
    UpdateUserRequest request,
    AuthDbContext db,
    ClaimsPrincipal actor,
    RefreshTokenService refreshTokenService) =>
{
    var user = await db.Users.Include(x => x.UserRoles).ThenInclude(x => x.Role).FirstOrDefaultAsync(x => x.Id == id);
    if (user is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { message = "Full name and email are required." });
    }

    if (await db.Users.AnyAsync(x => x.Id != id && x.Email == request.Email.Trim()))
    {
        return Results.Conflict(new { message = "Email already belongs to another account." });
    }

    var requestedRoleNames = request.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (requestedRoleNames.Length != 1 || !AuthRoles.IsKnown(requestedRoleNames[0]))
    {
        return Results.BadRequest(new { message = "Each account must have exactly one predefined actor role." });
    }

    var roles = await db.Roles.Where(x => requestedRoleNames.Contains(x.Name)).ToListAsync();
    if (roles.Count != requestedRoleNames.Length)
    {
        return Results.BadRequest(new { message = "One or more requested roles are invalid." });
    }

    var requestedRoleName = roles.Single().Name;
    var currentRoleName = user.UserRoles.SingleOrDefault()?.Role.Name;
    var targetIsSuperAdmin = currentRoleName == AuthRoles.Admin;
    if (!actor.IsInRole(AuthRoles.Admin)
        && (targetIsSuperAdmin || requestedRoleName == AuthRoles.Admin))
    {
        return Results.Forbid();
    }

    if (id == actor.GetUserId()
        && (!request.IsActive || !string.Equals(currentRoleName, requestedRoleName, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "You cannot deactivate your own account or change your own actor role." });
    }

    if (targetIsSuperAdmin && (!request.IsActive || requestedRoleName != AuthRoles.Admin))
    {
        var otherActiveSuperAdmins = await db.Users.CountAsync(x =>
            x.Id != id
            && x.IsActive
            && !x.IsLocked
            && x.UserRoles.Any(ur => ur.Role.Name == AuthRoles.Admin));
        if (otherActiveSuperAdmins == 0)
        {
            return Results.Conflict(new { message = "The final active ADMIN account cannot be deactivated or reassigned." });
        }
    }

    user.FullName = request.FullName.Trim();
    user.Email = request.Email.Trim();
    user.IsActive = request.IsActive;

    db.UserRoles.RemoveRange(user.UserRoles);
    db.UserRoles.AddRange(roles.Select(role => new UserRole { UserId = user.Id, RoleId = role.Id }));
    await db.SaveChangesAsync();
    if (!request.IsActive || !string.Equals(currentRoleName, requestedRoleName, StringComparison.OrdinalIgnoreCase))
    {
        await refreshTokenService.RevokeForUserAsync(user.Id);
    }

    return Results.Ok(ToSummary(user, withRoles: roles.Select(x => x.Name)));
});

users.MapPatch("/{id:int}/lock", async (
    int id,
    AuthDbContext db,
    ClaimsPrincipal actor,
    RefreshTokenService refreshTokenService) =>
{
    var user = await db.Users
        .Include(x => x.UserRoles)
        .ThenInclude(x => x.Role)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (user is null)
    {
        return Results.NotFound();
    }

    if (id == actor.GetUserId())
    {
        return Results.BadRequest(new { message = "You cannot lock your own account." });
    }

    var targetIsSuperAdmin = user.UserRoles.Any(x => x.Role.Name == AuthRoles.Admin);
    if (targetIsSuperAdmin && !actor.IsInRole(AuthRoles.Admin))
    {
        return Results.Forbid();
    }

    if (targetIsSuperAdmin && user.IsActive && !user.IsLocked)
    {
        var otherActiveSuperAdmins = await db.Users.CountAsync(x =>
            x.Id != id
            && x.IsActive
            && !x.IsLocked
            && x.UserRoles.Any(ur => ur.Role.Name == AuthRoles.Admin));
        if (otherActiveSuperAdmins == 0)
        {
            return Results.Conflict(new { message = "The final active ADMIN account cannot be locked." });
        }
    }

    user.IsLocked = true;
    await db.SaveChangesAsync();
    await refreshTokenService.RevokeForUserAsync(user.Id);
    return Results.NoContent();
});

users.MapPatch("/{id:int}/unlock", async (int id, AuthDbContext db, ClaimsPrincipal actor) =>
{
    var user = await db.Users
        .Include(x => x.UserRoles)
        .ThenInclude(x => x.Role)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (user is null)
    {
        return Results.NotFound();
    }

    if (id == actor.GetUserId())
    {
        return Results.BadRequest(new { message = "You cannot unlock your own account." });
    }

    if (user.UserRoles.Any(x => x.Role.Name == AuthRoles.Admin)
        && !actor.IsInRole(AuthRoles.Admin))
    {
        return Results.Forbid();
    }

    user.IsLocked = false;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

var rolesGroup = app.MapGroup("/api/roles")
    .WithTags("Roles")
    .RequireAuthorization(AuthPolicies.SystemAdministration);
rolesGroup.MapGet("/", async (AuthDbContext db) => Results.Ok(await db.Roles.OrderBy(x => x.Name).ToListAsync()));
rolesGroup.MapPost("/", async (CreateRoleRequest request, AuthDbContext db) =>
{
    var roleName = request.Name.Trim().ToUpperInvariant();
    if (!AuthRoles.IsKnown(roleName))
    {
        return Results.BadRequest(new { message = "Only predefined ClubReportHub actor roles are supported." });
    }

    if (await db.Roles.AnyAsync(x => x.Name == roleName))
    {
        return Results.Conflict(new { message = "Role already exists." });
    }

    var role = new Role { Name = roleName };
    db.Roles.Add(role);
    await db.SaveChangesAsync();
    return Results.Created($"/api/roles/{role.Id}", role);
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.ApplyMigrationsWithRetryAsync(logger);
    await AuthSeeder.SeedAsync(
        db,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
        builder.Configuration);
}

app.Run();

static UserSummary ToSummary(User user, IEnumerable<string>? withRoles = null)
{
    var roles = withRoles?.ToArray() ?? user.UserRoles.Select(x => x.Role.Name).OrderBy(x => x).ToArray();
    return new UserSummary(user.Id, user.Username, user.FullName, user.Email, roles, user.IsActive, user.IsLocked);
}

static bool HasValidActorConfiguration(User user)
{
    var roles = user.UserRoles
        .Select(x => x.Role.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return roles.Length == 1 && AuthRoles.IsKnown(roles[0]);
}
