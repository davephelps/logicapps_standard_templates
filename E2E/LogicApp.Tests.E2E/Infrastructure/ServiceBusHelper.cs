using Azure.Messaging.ServiceBus;

namespace LogicApp.Tests.E2E.Infrastructure;

public sealed class ServiceBusHelper : IAsyncDisposable
{
    private readonly string _connectionString;

    public ServiceBusHelper(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<ServiceBusReceivedMessage>> ReadMessages(
        string queueName, int expectedCount, TimeSpan timeout)
    {
        await using var client = new ServiceBusClient(_connectionString);
        await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
        });
        var messages = new List<ServiceBusReceivedMessage>();
        var deadline = DateTime.UtcNow + timeout;
        while (messages.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            var batch = await receiver.ReceiveMessagesAsync(
                maxMessages: expectedCount - messages.Count,
                maxWaitTime: TimeSpan.FromSeconds(5));
            messages.AddRange(batch);
        }
        return messages;
    }

    public async Task PurgeQueue(string queueName)
    {
        await using var client = new ServiceBusClient(_connectionString);
        await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
        });
        while (true)
        {
            var msgs = await receiver.ReceiveMessagesAsync(100, maxWaitTime: TimeSpan.FromSeconds(2));
            if (msgs.Count == 0) break;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
