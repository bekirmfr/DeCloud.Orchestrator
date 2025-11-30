using Microsoft.AspNetCore.SignalR;
using Orchestrator.Data;
using Orchestrator.Hubs;
using Orchestrator.Models;

namespace Orchestrator.Services;

public interface IEventService
{
    Task EmitAsync(OrchestratorEvent evt);
    Task EmitToUserAsync(string userId, OrchestratorEvent evt);
    Task EmitToNodeAsync(string nodeId, OrchestratorEvent evt);
}

public class EventService : IEventService
{
    private readonly DataStore _dataStore;
    private readonly IHubContext<OrchestratorHub> _hubContext;
    private readonly ILogger<EventService> _logger;

    public EventService(
        DataStore dataStore,
        IHubContext<OrchestratorHub> hubContext,
        ILogger<EventService> logger)
    {
        _dataStore = dataStore;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task EmitAsync(OrchestratorEvent evt)
    {
        // Store in history
        _dataStore.AddEvent(evt);

        // Broadcast to all connected clients
        await _hubContext.Clients.All.SendAsync("Event", evt);

        // Also send to resource-specific group
        await _hubContext.Clients.Group($"{evt.ResourceType}:{evt.ResourceId}")
            .SendAsync("Event", evt);

        _logger.LogDebug("Event emitted: {Type} for {ResourceType}/{ResourceId}", 
            evt.Type, evt.ResourceType, evt.ResourceId);
    }

    public async Task EmitToUserAsync(string userId, OrchestratorEvent evt)
    {
        _dataStore.AddEvent(evt);

        // Send to user's group
        await _hubContext.Clients.Group($"user:{userId}").SendAsync("Event", evt);

        _logger.LogDebug("Event emitted to user {UserId}: {Type}", userId, evt.Type);
    }

    public async Task EmitToNodeAsync(string nodeId, OrchestratorEvent evt)
    {
        _dataStore.AddEvent(evt);

        // Send to node's group
        await _hubContext.Clients.Group($"node:{nodeId}").SendAsync("Event", evt);

        _logger.LogDebug("Event emitted to node {NodeId}: {Type}", nodeId, evt.Type);
    }
}
