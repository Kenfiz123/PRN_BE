using System.Diagnostics;
using System.Text.Json;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationService.Data;
using NotificationService.Models;
using StackExchange.Redis;

namespace NotificationService.Consumers;

/// <summary>
/// Consumes events from Redis Streams using XREADGROUP.
/// Creates Notifications in the database and acknowledges messages after successful processing.
/// </summary>
public sealed class RedisStreamNotificationConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabase _db;
    private readonly RedisStreamOptions _options;
    private readonly string _consumerName;
    private readonly ILogger<RedisStreamNotificationConsumer> _logger;

    public RedisStreamNotificationConsumer(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisStreamOptions> options,
        ILogger<RedisStreamNotificationConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _db = connectionMultiplexer.GetDatabase();
        _consumerName = $"{_options.ConsumerNamePrefix}-{Environment.MachineName}-{Environment.ProcessId}";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        var maxInitRetries = 5;

        // Phase 1: Initialize connection and ensure consumer group
        for (var attempt = 1; attempt <= maxInitRetries; attempt++)
        {
            try
            {
                await EnsureConsumerGroupAsync(stoppingToken);
                // Phase 1b: Catch-up — read ALL existing messages in the stream
                // before switching to '>' (NewMessages) to avoid losing messages
                // published before the consumer group existed.
                await CatchUpExistingMessagesAsync(stoppingToken);
                _logger.LogInformation(
                    "Redis Stream consumer started. Stream: '{StreamName}', Group: '{ConsumerGroup}', Consumer: '{ConsumerName}'",
                    _options.StreamName,
                    _options.ConsumerGroup,
                    _consumerName);
                break;
            }
            catch (RedisException ex) when (attempt < maxInitRetries)
            {
                _logger.LogWarning(ex,
                    "Redis consumer failed to initialize (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s...",
                    attempt, maxInitRetries, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 1.5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Redis Stream consumer failed after {MaxRetries} attempts. Notification service will run without consuming events.",
                    maxInitRetries);
                return;
            }
        }

        // Phase 2: Main consumption loop — only new messages from here on
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadNewMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Redis consumer loop. Will retry after poll interval.");
                await Task.Delay(_options.PollIntervalMs, stoppingToken);
            }
        }
    }

    private async Task EnsureConsumerGroupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.StreamCreateConsumerGroupAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                StreamPosition.NewMessages,
                createStream: true);
            _logger.LogInformation("Created consumer group '{ConsumerGroup}' on stream '{StreamName}'",
                _options.ConsumerGroup, _options.StreamName);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Consumer group '{ConsumerGroup}' already exists on stream '{StreamName}'",
                _options.ConsumerGroup, _options.StreamName);
        }
    }

    /// <summary>
    /// Reads ALL existing messages in the stream (from beginning) during startup.
    /// This prevents losing messages that were published before the consumer group was created.
    /// After all existing messages are processed, the consumer switches to '>' (NewMessages).
    /// </summary>
    private async Task CatchUpExistingMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Read from the beginning of the stream (id "0-0")
            var entries = await _db.StreamReadGroupAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                _consumerName,
                StreamPosition.Beginning,
                count: _options.BatchSize);

            if (entries.Length == 0)
            {
                _logger.LogDebug("No existing messages in stream '{StreamName}' during catch-up", _options.StreamName);
                return;
            }

            _logger.LogInformation(
                "Catch-up phase: found {Count} existing messages in stream '{StreamName}'",
                entries.Length, _options.StreamName);

            var processed = 0;
            var errors = 0;
            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    await ProcessMessageAsync(entry, cancellationToken);
                    processed++;
                }
                catch
                {
                    errors++;
                    // Continue processing other messages
                }
            }

            _logger.LogInformation(
                "Catch-up phase completed. Processed: {Processed}, Errors: {Errors}",
                processed, errors);

            // If there are more messages, recursively catch up (paginate)
            if (processed > 0)
            {
                await CatchUpExistingMessagesAsync(cancellationToken);
            }
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // Group doesn't exist yet — this shouldn't happen since we create it above,
            // but handle gracefully
            _logger.LogWarning("Consumer group disappeared during catch-up. Will retry on next loop.");
        }
        catch (RedisException)
        {
            // Stream might not exist yet — that's fine, no catch-up needed
            _logger.LogDebug("Stream '{StreamName}' does not exist yet, skipping catch-up", _options.StreamName);
        }
    }

    private async Task ReadNewMessagesAsync(CancellationToken stoppingToken)
    {
        // '>' means: only messages delivered to this consumer group AFTER the last acknowledged message
        var entries = await _db.StreamReadGroupAsync(
            _options.StreamName,
            _options.ConsumerGroup,
            _consumerName,
            StreamPosition.NewMessages,
            count: _options.BatchSize);

        if (entries.Length == 0)
        {
            await Task.Delay(_options.PollIntervalMs, stoppingToken);
            return;
        }

        var batchStopwatch = Stopwatch.StartNew();
        foreach (var entry in entries)
        {
            if (stoppingToken.IsCancellationRequested) break;
            await ProcessMessageAsync(entry, stoppingToken);
        }
        batchStopwatch.Stop();

        _logger.LogDebug(
            "Processed batch of {Count} messages in {ElapsedMs}ms by consumer '{ConsumerName}'",
            entries.Length, batchStopwatch.ElapsedMilliseconds, _consumerName);
    }

    private async Task ProcessMessageAsync(StreamEntry entry, CancellationToken cancellationToken)
    {
        var redisEntryId = entry.Id;
        Guid eventId;
        string routingKey;
        string payload;

        try
        {
            var fields = entry.Values.ToDictionary(
                x => x.Name.ToString(),
                x => x.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

            if (!fields.TryGetValue("eventId", out var eventIdStr) || !Guid.TryParse(eventIdStr, out eventId))
            {
                _logger.LogWarning("Stream entry {RedisEntryId} has no valid eventId, acknowledging and skipping.", redisEntryId);
                await AcknowledgeMessageAsync(redisEntryId);
                return;
            }

            routingKey = fields.GetValueOrDefault("eventType", "");
            payload = fields.GetValueOrDefault("payload", "{}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read fields from stream entry {RedisEntryId}", redisEntryId);
            return; // Do not ACK — message stays pending
        }

        var msgStopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

            // Idempotency: skip if already processed
            var alreadyProcessed = await db.ProcessedEvents
                .AnyAsync(x => x.EventId == eventId, cancellationToken);
            if (alreadyProcessed)
            {
                _logger.LogDebug("Skipping duplicate event {EventId} (already processed)", eventId);
                await AcknowledgeMessageAsync(redisEntryId);
                return;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var notification = CreateNotification(routingKey, root);

            db.Notifications.Add(notification);
            db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = eventId,
                RoutingKey = routingKey,
                ProcessedAtUtc = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
            await AcknowledgeMessageAsync(redisEntryId);

            msgStopwatch.Stop();
            _logger.LogInformation(
                "Processed event {EventId} ({EventType}) -> Notification created. RedisEntryId: {RedisEntryId}. Duration: {DurationMs}ms",
                eventId, routingKey, redisEntryId, msgStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            msgStopwatch.Stop();
            _logger.LogError(ex,
                "Failed to process event {EventId} ({EventType}) from RedisEntryId {RedisEntryId}. Message remains pending.",
                eventId, routingKey, redisEntryId);
            // Do NOT acknowledge — message stays in pending list for retry/recovery
        }
    }

    private async Task AcknowledgeMessageAsync(RedisValue entryId)
    {
        await _db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entryId);
    }

    private static Notification CreateNotification(string routingKey, JsonElement root)
    {
        return routingKey switch
        {
            EventRoutingKeys.ClubCreated => new Notification
            {
                RecipientRole = AuthRoles.StudentAffairsAdmin,
                EventType = routingKey,
                Title = "New club created",
                Message = $"{GetString(root, "clubName")} ({GetString(root, "clubCode")}) has been added to the system."
            },
            EventRoutingKeys.UserRegistered => new Notification
            {
                RecipientUserId = GetInt(root, "userId"),
                EventType = routingKey,
                Title = "Welcome to FPTU Club Hub",
                Message = $"Hello {GetString(root, "fullName")}. Your member account is ready."
            },
            EventRoutingKeys.ActivityCreated => new Notification
            {
                RecipientRole = AuthRoles.ClubMember,
                EventType = routingKey,
                Title = "New activity",
                Message = $"{GetString(root, "clubName")} has scheduled the activity {GetString(root, "title")}."
            },
            EventRoutingKeys.ReportSubmitted => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                RecipientRole = GetInt(root, "recipientUserId") is null
                    ? (GetString(root, "status") == "Submitted" ? AuthRoles.ClubManager : AuthRoles.StudentAffairsAdmin)
                    : null,
                EventType = routingKey,
                Title = GetString(root, "status") == "Submitted" ? "Report awaiting club manager review" : "Report awaiting final approval",
                Message = $"{GetString(root, "clubName")} submitted the report for period {GetString(root, "period")}."
            },
            EventRoutingKeys.ReportApproved => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                EventType = routingKey,
                Title = "Report approved",
                Message = $"The {GetString(root, "period")} report for {GetString(root, "clubName")} has been approved."
            },
            EventRoutingKeys.ReportRejected => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                EventType = routingKey,
                Title = "Report requires revision",
                Message = $"The {GetString(root, "period")} report for {GetString(root, "clubName")} was rejected: {GetString(root, "feedback")}"
            },
            EventRoutingKeys.KpiCalculated => new Notification
            {
                RecipientRole = AuthRoles.ClubManager,
                EventType = routingKey,
                Title = "KPI updated",
                Message = $"The KPI score for {GetString(root, "clubName")} in period {GetString(root, "period")} is {GetString(root, "points")}."
            },
            EventRoutingKeys.BudgetProposalSubmitted => new Notification
            {
                RecipientRole = AuthRoles.StudentAffairsAdmin,
                EventType = routingKey,
                Title = "New budget proposal",
                Message = $"{GetString(root, "clubName")} requested a budget of {GetString(root, "requestedAmount")} VND."
            },
            EventRoutingKeys.BudgetApproved => new Notification
            {
                RecipientUserId = GetInt(root, "recipientUserId"),
                EventType = routingKey,
                Title = "Budget approved",
                Message = $"The budget for {GetString(root, "clubName")} was approved for {GetString(root, "approvedAmount")} VND."
            },
            EventRoutingKeys.SettlementOverdue => new Notification
            {
                RecipientRole = AuthRoles.Treasurer,
                EventType = routingKey,
                Title = "Overdue settlement",
                Message = $"{GetString(root, "clubName")} has an overdue settlement for proposal #{GetString(root, "proposalId")}."
            },
            EventRoutingKeys.ExportCompleted => new Notification
            {
                RecipientUserId = GetInt(root, "requestedByUserId"),
                EventType = routingKey,
                Title = "Export ready",
                Message = $"The {GetString(root, "exportType")} export is ready: {GetString(root, "fileName")}."
            },
            EventRoutingKeys.ReportDeadlineReminder => new Notification
            {
                RecipientRole = AuthRoles.StudentAffairsAdmin,
                EventType = routingKey,
                Title = "Report deadline reminder",
                Message = $"Some clubs have not yet submitted their reports for period {GetString(root, "period")}."
            },
            _ => new Notification
            {
                RecipientRole = AuthRoles.Admin,
                EventType = routingKey,
                Title = "System event",
                Message = "The system recorded a new event."
            }
        };
    }

    private static string GetString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) ? value.ToString() : string.Empty;
    }

    private static int? GetInt(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;
    }
}
