using Orchestrator.Models;

namespace Orchestrator.Services.Reconciliation;

/// <summary>
/// Handles execution of a specific obligation type.
/// Each handler is registered for one or more obligation types
/// and is responsible for converging actual state toward desired state.
///
/// Handlers MUST be idempotent. The reconciliation loop may call Execute()
/// multiple times for the same obligation (after retries, restarts, etc.).
///
/// Implementation guidelines:
///   1. Check if the work is already done (idempotency guard)
///   2. Do the minimum work needed to make progress
///   3. Return the appropriate ObligationResult
///   4. Never throw — return Retry or Fail instead
///   5. Use SignalKey for async operations (command acks, etc.)
/// </summary>
public interface IObligationHandler
{
    /// <summary>
    /// The obligation type(s) this handler can process.
    /// Convention: "{domain}.{action}" (e.g., "vm.provision")
    /// </summary>
    IReadOnlyList<string> SupportedTypes { get; }

    /// <summary>
    /// Execute one step of the obligation.
    /// Called by the reconciliation loop when the obligation is ready.
    /// </summary>
    Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct);
}

/// <summary>
/// Routes obligations to their type-specific handlers.
/// Registered as a singleton — collects all IObligationHandler implementations via DI.
/// </summary>
public class ObligationDispatcher
{
    private readonly Dictionary<string, IObligationHandler> _handlers;
    private readonly ILogger<ObligationDispatcher> _logger;

    public ObligationDispatcher(
        IEnumerable<IObligationHandler> handlers,
        ILogger<ObligationDispatcher> logger)
    {
        _logger = logger;
        _handlers = new Dictionary<string, IObligationHandler>();

        foreach (var handler in handlers)
        {
            foreach (var type in handler.SupportedTypes)
            {
                if (_handlers.TryGetValue(type, out var existing))
                {
                    _logger.LogWarning(
                        "Duplicate handler for obligation type {Type}: {Existing} vs {New}. Using {New}.",
                        type, existing.GetType().Name, handler.GetType().Name);
                }
                _handlers[type] = handler;
            }
        }

        _logger.LogInformation(
            "ObligationDispatcher initialized with {Count} handler(s) covering {Types} type(s)",
            handlers.Count(), _handlers.Count);
    }

    /// <summary>
    /// Dispatch an obligation to its handler.
    /// Returns null if no handler is registered for the obligation type.
    /// </summary>
    public async Task<ObligationResult?> DispatchAsync(Obligation obligation, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(obligation.Type, out var handler))
        {
            _logger.LogError(
                "No handler registered for obligation type {Type} (obligation {Id})",
                obligation.Type, obligation.Id);
            return null;
        }

        try
        {
            return await handler.ExecuteAsync(obligation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Obligation {Id} cancelled via token", obligation.Id);
            return ObligationResult.Retry("Cancelled — will retry next tick");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception in handler for obligation {Obligation}",
                obligation);
            return ObligationResult.Retry($"Handler threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a handler exists for the given obligation type
    /// </summary>
    public bool HasHandler(string obligationType) => _handlers.ContainsKey(obligationType);

    /// <summary>
    /// Get all registered obligation types
    /// </summary>
    public IReadOnlyCollection<string> RegisteredTypes => _handlers.Keys;
}
