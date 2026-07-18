using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Models;

namespace ClubReportHub.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public void ProcessedEvent_ShouldUseEventIdAsItsPrimaryKey()
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new NotificationDbContext(options);
        var entity = db.Model.FindEntityType(typeof(ProcessedEvent));
        var primaryKey = entity?.FindPrimaryKey();

        Assert.NotNull(primaryKey);
        Assert.Collection(
            primaryKey!.Properties,
            property => Assert.Equal(nameof(ProcessedEvent.EventId), property.Name));
    }
}
