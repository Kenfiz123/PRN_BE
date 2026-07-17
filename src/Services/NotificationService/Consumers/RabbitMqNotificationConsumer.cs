using System.Text;
using System.Text.Json;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationService.Data;
using NotificationService.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NotificationService.Consumers;

public sealed class RabbitMqNotificationConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqNotificationConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;
    private IModel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int maxRetries = 5;
        var retryDelay = TimeSpan.FromSeconds(5);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await InitializeRabbitMqAsync(stoppingToken);
                logger.LogInformation("RabbitMQ consumer started successfully on queue notification-service");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                logger.LogWarning(ex,
                    "RabbitMQ consumer failed to start (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s...",
                    attempt, maxRetries, retryDelay.TotalSeconds);

                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 1.5));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "RabbitMQ consumer failed to start after {MaxRetries} attempts. Notification service will run without consuming events.",
                    maxRetries);
                return;
            }
        }
    }

    private void InitializeRabbitMqAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(_options.ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false);
        _channel.ExchangeDeclare($"{_options.ExchangeName}.dead", ExchangeType.Topic, durable: true, autoDelete: false);
        _channel.QueueDeclare(
            queue: "notification-service",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object> { ["x-dead-letter-exchange"] = $"{_options.ExchangeName}.dead" });
        _channel.QueueDeclare("notification-service.dead", durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind("notification-service.dead", $"{_options.ExchangeName}.dead", "#");

        foreach (var key in new[]
        {
            EventRoutingKeys.UserRegistered,
            EventRoutingKeys.ClubCreated,
            EventRoutingKeys.ActivityCreated,
            EventRoutingKeys.ReportSubmitted,
            EventRoutingKeys.ReportApproved,
            EventRoutingKeys.ReportRejected,
            EventRoutingKeys.KpiCalculated,
            EventRoutingKeys.BudgetProposalSubmitted,
            EventRoutingKeys.BudgetApproved,
            EventRoutingKeys.SettlementOverdue,
            EventRoutingKeys.ExportCompleted,
            EventRoutingKeys.ReportDeadlineReminder
        })
        {
            _channel.QueueBind("notification-service", _options.ExchangeName, key);
        }

        _channel.BasicQos(0, 5, false);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, args) =>
        {
            try
            {
                await ProcessMessageAsync(args, stoppingToken);
                _channel.BasicAck(args.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process RabbitMQ message {DeliveryTag}", args.DeliveryTag);
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume("notification-service", autoAck: false, consumer);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetString(args.Body.ToArray());
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventId = root.TryGetProperty("eventId", out var eventIdElement)
            ? eventIdElement.GetGuid()
            : Guid.NewGuid();
        var routingKey = args.RoutingKey;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

        // Check for duplicate processing
        var alreadyProcessed = await db.ProcessedEvents
            .AnyAsync(x => x.EventId == eventId, cancellationToken);
        if (alreadyProcessed)
        {
            logger.LogDebug("Skipping duplicate event {EventId}", eventId);
            return;
        }

        var notification = CreateNotification(routingKey, root);
        db.Notifications.Add(notification);
        db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = eventId,
            RoutingKey = routingKey,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Processed event {EventId} with routing key {RoutingKey}", eventId, routingKey);
    }

    private static Notification CreateNotification(string routingKey, JsonElement root)
    {
        return routingKey switch
        {
            EventRoutingKeys.ClubCreated => new Notification
            {
                RecipientRole = AuthRoles.Admin,
                EventType = routingKey,
                Title = "Câu lạc bộ mới được thành lập",
                Message = $"{GetString(root, "clubName")} ({GetString(root, "clubCode")}) đã được đưa vào hệ thống."
            },
            EventRoutingKeys.UserRegistered => new Notification
            {
                RecipientUserId = GetInt(root, "userId"),
                EventType = routingKey,
                Title = "Chào mừng đến FPTU Club Hub",
                Message = $"Xin chào {GetString(root, "fullName")}. Tài khoản thành viên của bạn đã sẵn sàng."
            },
            EventRoutingKeys.ActivityCreated => new Notification
            {
                RecipientRole = AuthRoles.ClubMember,
                EventType = routingKey,
                Title = "Hoạt động mới",
                Message = $"{GetString(root, "clubName")} đã lên lịch hoạt động {GetString(root, "title")}."
            },
            EventRoutingKeys.ReportSubmitted => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                RecipientRole = GetInt(root, "recipientUserId") is null
                    ? (GetString(root, "status") == "Submitted" ? AuthRoles.ClubManager : AuthRoles.Admin)
                    : null,
                EventType = routingKey,
                Title = GetString(root, "status") == "Submitted" ? "Báo cáo chờ chủ nhiệm" : "Báo cáo chờ phê duyệt",
                Message = $"{GetString(root, "clubName")} đã gửi báo cáo kỳ {GetString(root, "period")}."
            },
            EventRoutingKeys.ReportApproved => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                EventType = routingKey,
                Title = "Báo cáo đã được phê duyệt",
                Message = $"Báo cáo kỳ {GetString(root, "period")} của {GetString(root, "clubName")} đã được phê duyệt."
            },
            EventRoutingKeys.ReportRejected => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                EventType = routingKey,
                Title = "Báo cáo cần chỉnh sửa",
                Message = $"Báo cáo kỳ {GetString(root, "period")} của {GetString(root, "clubName")} bị từ chối: {GetString(root, "feedback")}"
            },
            EventRoutingKeys.KpiCalculated => new Notification
            {
                RecipientRole = AuthRoles.ClubManager,
                EventType = routingKey,
                Title = "KPI đã được cập nhật",
                Message = $"KPI kỳ {GetString(root, "period")} của {GetString(root, "clubName")} là {GetString(root, "points")} điểm."
            },
            EventRoutingKeys.BudgetProposalSubmitted => new Notification
            {
                RecipientRole = AuthRoles.StudentAffairsAdmin,
                EventType = routingKey,
                Title = "Đề xuất ngân sách mới",
                Message = $"{GetString(root, "clubName")} đề xuất ngân sách {GetString(root, "requestedAmount")} VNĐ."
            },
            EventRoutingKeys.BudgetApproved => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                EventType = routingKey,
                Title = "Ngân sách đã được phê duyệt",
                Message = $"Ngân sách của {GetString(root, "clubName")} được duyệt với số tiền {GetString(root, "approvedAmount")} VNĐ."
            },
            EventRoutingKeys.SettlementOverdue => new Notification
            {
                RecipientRole = AuthRoles.Treasurer,
                EventType = routingKey,
                Title = "Quyết toán quá hạn",
                Message = $"{GetString(root, "clubName")} có quyết toán quá hạn cho đề xuất #{GetString(root, "proposalId")}."
            },
            EventRoutingKeys.ExportCompleted => new Notification
            {
                RecipientUserId = GetInt(root, "requestedByUserId"),
                EventType = routingKey,
                Title = "File xuất đã sẵn sàng",
                Message = $"File {GetString(root, "exportType")} đã tạo xong: {GetString(root, "fileName")}."
            },
            EventRoutingKeys.ReportDeadlineReminder => new Notification
            {
                RecipientRole = AuthRoles.Admin,
                EventType = routingKey,
                Title = "Nhắc hạn báo cáo",
                Message = $"Kỳ {GetString(root, "period")} vẫn còn câu lạc bộ chưa nộp báo cáo."
            },
            _ => new Notification
            {
                RecipientRole = AuthRoles.Admin,
                EventType = routingKey,
                Title = "Sự kiện hệ thống",
                Message = "Hệ thống vừa ghi nhận một sự kiện mới."
            }
        };
    }

    private static string GetString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) ? value.ToString() : string.Empty;
    }

    private static int? GetInt(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    }
}
