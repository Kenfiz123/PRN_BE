using ActivityService.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityService.Data;

public sealed class ActivityDbContext(DbContextOptions<ActivityDbContext> options) : DbContext(options)
{
    public DbSet<ClubActivity> Activities => Set<ClubActivity>();
    public DbSet<ActivityParticipant> ActivityParticipants => Set<ActivityParticipant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClubActivity>(entity =>
        {
            entity.HasIndex(x => new { x.ClubId, x.StartTimeUtc });
            entity.HasIndex(x => x.Status);
            entity.Property(x => x.ClubName).HasMaxLength(200);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.Location).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.HasMany(x => x.Participants)
                .WithOne(x => x.Activity)
                .HasForeignKey(x => x.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActivityParticipant>(entity =>
        {
            entity.HasIndex(x => new { x.ActivityId, x.UserId }).IsUnique();
            entity.Property(x => x.FullName).HasMaxLength(200);
            entity.Property(x => x.AttendanceStatus).HasMaxLength(40);
        });
    }
}
