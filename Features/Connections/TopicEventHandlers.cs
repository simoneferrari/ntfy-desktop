using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Topics.Events;

namespace NtfyDesktop.Features.Connections;

public class RemoveConnectionWhenTopicDeleted(ConnectionManager connectionManager) : IEventHandler<TopicDeleted>
{
    public Task HandleAsync(TopicDeleted eventModel, CancellationToken ct)
        => connectionManager.RemoveTopicConnectionAsync(eventModel.TopicId);
}

public class UpdateConnectionWhenTopicUpdated(ConnectionManager connectionManager) : IEventHandler<TopicUpdated>
{
    public async Task HandleAsync(TopicUpdated eventModel, CancellationToken ct)
    {
        var topic = eventModel.Topic;
        connectionManager.GetTopicConnection(topic.Id, out var conn);

        if (topic.Enabled) {
            
            if (conn is null)
                await connectionManager.AddTopicConnectionAsync(topic);

            else if (!conn.MatchesTopicSettings(topic)) {
                await connectionManager.RebuildTopicConnectionAsync(topic.Id);
            }
            
        }
        else {
            
            if (conn is null) return;
            await connectionManager.RemoveTopicConnectionAsync(topic.Id);
        }

    }
}

public class BuildConnectionWhenTopicAdded(ConnectionManager connectionManager) : IEventHandler<TopicAdded>
{
    public async Task HandleAsync(TopicAdded eventModel, CancellationToken ct)
    {
        var topic = eventModel.Topic;

        if (!topic.Enabled) return;

        await connectionManager.AddTopicConnectionAsync(topic);
    }
}

