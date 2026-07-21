using System.Text;
using System.Text.Json;
using ClubReportHub.Shared.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ClubReportHub.Shared.Messaging;

/// <summary>
/// Redis Streams implementation of <see cref="IEventBus"/>.
/// Uses XADD to append events to a stream and stores the routing key as a stream field.
/// </summary>
public sealed class RedisStreamEventBus(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<RedisStreamOptions> options,
    ILogger<RedisStreamEventBus> logger) : IEventBus
{
    private readonly RedisStreamOptions _options = options.Value;
    private readonly IDatabase _db = connectionMultiplexer.GetDatabase();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync<TEvent>(TEvent integrationEvent, string routingKey, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        var retryDelay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs);
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                var payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), JsonOptions);

                var values = new NameValueEntry[]
                {
                    new("eventId", integrationEvent.EventId.ToString()),
                    new("eventType", routingKey),
                    new("occurredAtUtc", integrationEvent.OccurredAtUtc.ToString("O")),
                    new("schemaVersion", "1.0"),
                    new("payload", payload)
                };

                // Extract correlation ID from the event if present
                if (integrationEvent is ReportSubmittedEvent rse)
                {
                    values = [
                        .. values,
                        new("entityId", rse.ReportId.ToString()),
                        new("clubId", rse.ClubId.ToString())
                    ];
                }
                else if (integrationEvent is ReportApprovedEvent rae)
                {
                    values = [
                        .. values,
                        new("entityId", rae.ReportId.ToString()),
                        new("clubId", rae.ClubId.ToString())
                    ];
                }
                else if (integrationEvent is ReportRejectedEvent rre)
                {
                    values = [
                        .. values,
                        new("entityId", rre.ReportId.ToString()),
                        new("clubId", rre.ClubId.ToString())
                    ];
                }
                else if (integrationEvent is ReportDeadlineReminderEvent rdre)
                {
                    values = [
                        .. values,
                        new("period", rdre.Period)
                    ];
                }
                else if (integrationEvent is ClubCreatedEvent cce)
                {
                    values = [
                        .. values,
                        new("entityId", cce.ClubId.ToString())
                    ];
                }
                else if (integrationEvent is ActivityCreatedEvent ace)
                {
                    values = [
                        .. values,
                        new("entityId", ace.ActivityId.ToString()),
                        new("clubId", ace.ClubId.ToString())
                    ];
                }
                else if (integrationEvent is KpiCalculatedEvent kce)
                {
                    values = [
                        .. values,
                        new("entityId", kce.ClubId.ToString()),
                        new("clubId", kce.ClubId.ToString())
                    ];
                }

                var redisEntryId = await _db.StreamAddAsync(
                    _options.StreamName,
                    values,
                    flags: CommandFlags.None);

                logger.LogInformation(
                    "Published integration event {EventId} ({EventType}) to stream '{StreamName}' with RedisEntryId {RedisEntryId}",
                    integrationEvent.EventId,
                    routingKey,
                    _options.StreamName,
                    redisEntryId);

                return;
            }
            catch (RedisException ex)
            {
                if (attempt >= _options.MaxRetries)
                {
                    logger.LogError(
                        ex,
                        "Failed to publish event {EventId} ({EventType}) after {MaxRetries} attempts. Stream: {StreamName}",
                        integrationEvent.EventId,
                        routingKey,
                        _options.MaxRetries,
                        _options.StreamName);
                    throw;
                }

                logger.LogWarning(
                    ex,
                    "Redis error publishing event {EventId} ({EventType}), attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms...",
                    integrationEvent.EventId,
                    routingKey,
                    attempt,
                    _options.MaxRetries,
                    retryDelay.TotalMilliseconds);

                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 10000));
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unexpected error publishing event {EventId} ({EventType})",
                    integrationEvent.EventId,
                    routingKey);
                throw;
            }
        }
    }
}
