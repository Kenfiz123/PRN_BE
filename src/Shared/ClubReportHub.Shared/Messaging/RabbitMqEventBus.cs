using System.Text;
using System.Text.Json;
using ClubReportHub.Shared.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ClubReportHub.Shared.Messaging;

public sealed class RabbitMqEventBus(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqEventBus> logger) : IEventBus
{
    private readonly RabbitMqOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync<TEvent>(TEvent integrationEvent, string routingKey, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                DispatchConsumersAsync = false
            };

            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.ExchangeDeclare(_options.ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false);

            var payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), JsonOptions);
            var body = Encoding.UTF8.GetBytes(payload);
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.MessageId = integrationEvent.EventId.ToString();
            properties.Timestamp = new AmqpTimestamp(integrationEvent.OccurredAtUtc.ToUnixTimeSeconds());

            channel.BasicPublish(
                exchange: _options.ExchangeName,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body);

            logger.LogInformation("Published integration event {EventId} with routing key {RoutingKey}", integrationEvent.EventId, routingKey);
        }
        catch (BrokerUnreachableException ex)
        {
            logger.LogWarning(ex, "RabbitMQ is unavailable. Event {EventId} was not published.", integrationEvent.EventId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish event {EventId}", integrationEvent.EventId);
            throw;
        }

        return Task.CompletedTask;
    }
}
