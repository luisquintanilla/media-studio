using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VideoProduction.Decisions.Experimental;

namespace VideoProduction.Providers.Decisions;

/// <summary>
/// Ordered decision failover: each link is tried in order and a provider failure, protocol error or timeout logs a
/// warning and moves to the next link. Caller cancellation is never swallowed. Owns and disposes the chain.
/// </summary>
public sealed class FallbackDecisionClient : IDecisionClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private readonly IDecisionClient[] chain;
    private readonly ILogger logger;
    private readonly TimeSpan? timeout;

    public FallbackDecisionClient(IReadOnlyList<IDecisionClient> chain, ILogger? logger = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0 || chain.Any(c => c is null)) throw new ArgumentException("A non-empty decision chain is required.", nameof(chain));
        this.chain = chain.ToArray();
        this.logger = logger ?? NullLogger.Instance;
        this.timeout = timeout ?? DefaultTimeout;
    }

    public IReadOnlyList<IDecisionClient> Chain => chain;

    public async Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<Exception>? failures = null;
        for (var i = 0; i < chain.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } limit && limit != Timeout.InfiniteTimeSpan) linked.CancelAfter(limit);
            try
            {
                var task = chain[i].GetResponseAsync(request, options, linked.Token);
                return timeout is { } wait && wait != Timeout.InfiniteTimeSpan
                    ? await task.WaitAsync(wait, cancellationToken).ConfigureAwait(false)
                    : await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                (failures ??= []).Add(ex);
                logger.LogWarning("Decision provider {Provider} failed ({Error}); trying the next decider.",
                    Name(chain[i]), ex is TimeoutException or OperationCanceledException ? "timeout" : ex.GetType().Name);
            }
        }
        throw new DecisionClientException("Every configured decision provider failed.", isTransient: false,
            new AggregateException(failures!));
    }

    /// <summary>Provider name from <see cref="DecisionClientMetadata"/>, else the type name.</summary>
    public static string Name(IDecisionClient client) =>
        (client.GetService(typeof(DecisionClientMetadata)) as DecisionClientMetadata)?.ProviderName ?? client.GetType().Name;

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : chain.Select(c => c.GetService(serviceType, serviceKey)).FirstOrDefault(s => s is not null);

    public void Dispose()
    {
        foreach (var client in chain) client.Dispose();
    }
}

/// <summary>Defers loading a decision client until first use; a load failure surfaces as <see cref="DecisionClientException"/>.</summary>
public sealed class LazyDecisionClient(string providerName, Func<IDecisionClient> factory) : IDecisionClient
{
    private readonly Lazy<IDecisionClient> inner = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly DecisionClientMetadata metadata = new(providerName);

    public async Task<DecisionResponse> GetResponseAsync(DecisionRequest request, DecisionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IDecisionClient client;
        try { client = inner.Value; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DecisionClientException($"The {providerName} decision provider could not be loaded.", isTransient: false, ex);
        }
        return await client.GetResponseAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(DecisionClientMetadata) && !inner.IsValueCreated ? metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : inner.IsValueCreated ? inner.Value.GetService(serviceType, serviceKey) : null;

    public void Dispose()
    {
        if (inner.IsValueCreated) inner.Value.Dispose();
    }
}
