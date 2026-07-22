using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ClubReportHub.Shared.Messaging;
using NotificationService.Consumers;
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

    [Fact]
    public void ActivityCreated_ShouldCreateOneNotificationPerDistinctRecipient()
    {
        using var payload = JsonDocument.Parse("""
            {
              "clubName": "AI Club",
              "title": "Weekly meeting",
              "recipientUserIds": [11001, 11002, 11002]
            }
            """);

        var notifications = RedisStreamNotificationConsumer.CreateNotifications(
            EventRoutingKeys.ActivityCreated,
            payload.RootElement);

        Assert.Equal(2, notifications.Count);
        Assert.Equal(new int?[] { 11001, 11002 }, notifications.Select(x => x.RecipientUserId).Order().ToArray());
        Assert.All(notifications, item => Assert.Null(item.RecipientRole));
    }

    [Fact]
    public void ReportCreated_ShouldNotifyTheAuthorAndClubManagers()
    {
        using var payload = JsonDocument.Parse("""
            {
              "clubName": "AI Club",
              "period": "2026-Q3",
              "recipientUserIds": [11001, 11003]
            }
            """);

        var notifications = RedisStreamNotificationConsumer.CreateNotifications(
            EventRoutingKeys.ReportCreated,
            payload.RootElement);

        Assert.Equal(new int?[] { 11001, 11003 }, notifications.Select(x => x.RecipientUserId).Order().ToArray());
        Assert.All(notifications, item => Assert.Equal("Bản nháp báo cáo mới", item.Title));
    }

    [Fact]
    public void FutureEventSubmission_ShouldNotifyEveryClubTreasurer()
    {
        using var payload = JsonDocument.Parse("""
            {
              "clubName": "AI Club",
              "period": "AI Hackathon",
              "workflowStage": "FinanceReview",
              "recipientUserIds": [12001, 12002]
            }
            """);

        var notifications = RedisStreamNotificationConsumer.CreateNotifications(
            EventRoutingKeys.ReportSubmitted,
            payload.RootElement);

        Assert.Equal(new int?[] { 12001, 12002 }, notifications.Select(x => x.RecipientUserId).Order().ToArray());
        Assert.All(notifications, item => Assert.Equal("Báo cáo sự kiện sắp tới cần lập ngân sách", item.Title));
    }

    [Fact]
    public void CombinedFutureEventPackage_ShouldNotifyFinalReviewers()
    {
        using var payload = JsonDocument.Parse("""
            {
              "clubName": "AI Club",
              "period": "AI Hackathon",
              "workflowStage": "FinalReview"
            }
            """);

        var notification = Assert.Single(RedisStreamNotificationConsumer.CreateNotifications(
            EventRoutingKeys.ReportSubmitted,
            payload.RootElement));

        Assert.Equal("STUDENT_AFFAIRS_ADMIN", notification.RecipientRole);
        Assert.Equal("Hồ sơ sự kiện đang chờ phê duyệt cuối", notification.Title);
    }
}
