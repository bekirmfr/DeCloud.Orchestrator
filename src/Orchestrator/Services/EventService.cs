using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Services;

public interface IEventService
{
    Task EmitAsync(OrchestratorEvent evt);
    Task<List<OrchestratorEvent>> GetEventsAsync(int limit = 100, EventType? type = null);
}

public class EventService : IEventService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<EventService> _logger;

    public EventService(DataStore dataStore, ILogger<EventService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Emit an event to the event stream
    /// </summary>
    public async Task EmitAsync(OrchestratorEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Id))
        {
            evt.Id = Guid.NewGuid().ToString();
        }

        if (evt.Timestamp == default)
        {
            evt.Timestamp = DateTime.UtcNow;
        }

        // Use SaveEventAsync instead of AddEvent
        await _dataStore.SaveEventAsync(evt);

        _logger.LogDebug(
            "Event emitted: {EventType} - {ResourceType}/{ResourceId}",
            evt.Type,
            evt.ResourceType,
            evt.ResourceId);
    }

    /// <summary>
    /// Get recent events
    /// </summary>
    public Task<List<OrchestratorEvent>> GetEventsAsync(int limit = 100, EventType? type = null)
    {
        var events = _dataStore.EventHistory
            .Reverse()
            .Take(limit);

        if (type.HasValue)
        {
            events = events.Where(e => e.Type == type.Value);
        }

        return Task.FromResult(events.ToList());
    }
}