using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Topics.Events;

namespace NtfyDesktop.Features.Topics;

// Coordinates topic lifecycle (add / edit / remove / enable). Persists to
// AppSettings and publishes a domain event; the connection side reacts to those
// events (see Features.Connections.TopicEventHandlers). Pure ordering/grouping
// logic lives in TopicArrangement.
public sealed class TopicManager(
    AppSettings settings,
    HistoryRepository history,
    TopicArrangement topicArrangement)
{
    public TopicArrangement Arrangement { get; } = topicArrangement;

    public void AddOrUpdate(TopicSettings edited, TopicSettings? original)
    {
        if (original is not null)
        {
            // Editing: keep the original's stable identity. Server + display name now
            // come from the editor dialog (the user may move the topic to another
            // server or rename its label).
            edited.Id = original.Id;

            var idx = settings.Topics.IndexOf(original);
            if (idx >= 0) settings.Topics[idx] = edited;
        }
        else
        {
            // New topic: the dialog preselects the default server, but fall back just
            // in case it came through unset.
            if (edited.ServerId == Guid.Empty)
                edited.ServerId = settings.DefaultServerId;

            settings.Topics.Add(edited);
        }

        Arrangement.SyncGroupOrder();
        settings.Save();

        // Publish the concrete event type — PublishAsync infers the bus envelope from
        // the static type, so a base-typed `IEvent` variable would publish as
        // EventEnvelope<IEvent> and match no Subscribe<TopicAdded/TopicUpdated>.
        if (original is null)
            _ = new TopicAdded(edited).PublishAsync();
        else
            _ = new TopicUpdated(edited).PublishAsync();
    }

    // Remove a topic. When deleteHistory is false the messages are kept and stay
    // browsable under "All topics" (mirrors server removal); when true they're
    // purged by topic id.
    public void Remove(TopicSettings topic, bool deleteHistory)
    {
        settings.Topics.Remove(topic);
        Arrangement.SyncGroupOrder();
        settings.Save();

        if (deleteHistory)
            history.DeleteByTopicId(topic.Id, MessageDeletionSource.Removal);

        _ = new TopicDeleted(topic.Id).PublishAsync();
    }

    // Flip a topic's Enabled flag. The connection side reacts to TopicUpdated by
    // starting/stopping the socket.
    public void ToggleEnabled(TopicSettings topic)
    {
        topic.Enabled = !topic.Enabled;
        settings.Save();

        _ = new TopicUpdated(topic).PublishAsync();
    }
}
