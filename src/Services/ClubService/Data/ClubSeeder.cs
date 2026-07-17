using Microsoft.Extensions.Logging;

namespace ClubService.Data;

public static class ClubSeeder
{
    // NOTE: Club seeding is intentionally left empty.
    // Clubs are created through the club creation application workflow,
    // not through database seeding. This ensures proper workflow and audit trail.
    public static Task SeedAsync(ClubDbContext db, ILogger? logger = null)
    {
        logger?.LogInformation("ClubSeeder: No static seed data needed - clubs are created via application workflow");
        return Task.CompletedTask;
    }
}
