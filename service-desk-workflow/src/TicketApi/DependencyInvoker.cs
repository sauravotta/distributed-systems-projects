using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TicketApi;

public sealed class DependencyInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<DependencyInvoker> logger)
{
    private const int MaximumAttempts = 3;
    private readonly ConcurrentDictionary<string, DependencyCircuitBreaker> _breakers = new(StringComparer.Ordinal);

    public async Task<TResponse> SendJsonAsync<TResponse>(
        string dependency,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var breaker = _breakers.GetOrAdd(dependency, _ => new DependencyCircuitBreaker());
        if (!breaker.TryEnter())
        {
            logger.LogWarning("Circuit breaker for {Dependency} is open; rejecting call without retry", dependency);
            throw new DependencyUnavailableException(dependency, "Circuit breaker is open.");
        }

        Exception? lastFailure = null;
        try
        {
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    using var request = requestFactory();
                    using var response = await httpClientFactory
                        .CreateClient(dependency)
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
                        if (result is null)
                        {
                            throw new JsonException($"The {dependency} service returned an empty response.");
                        }

                        breaker.RecordSuccess();
                        return result;
                    }

                    if (!IsRetryable(response.StatusCode))
                    {
                        breaker.RecordSuccess();
                        throw new DependencyRejectedException(dependency, response.StatusCode);
                    }

                    lastFailure = new HttpRequestException(
                        $"{dependency} returned {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);
                }
                catch (HttpRequestException exception)
                {
                    lastFailure = exception;
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailure = exception;
                }
                catch (JsonException exception)
                {
                    lastFailure = exception;
                }

                if (attempt < MaximumAttempts)
                {
                    var delay = TimeSpan.FromMilliseconds(100 * attempt);
                    logger.LogWarning(
                        lastFailure,
                        "Call to {Dependency} failed on attempt {Attempt}; retrying after {DelayMs} ms",
                        dependency,
                        attempt,
                        delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            breaker.Abandon();
            throw;
        }

        if (breaker.RecordFailure())
        {
            logger.LogError(
                "Circuit breaker for {Dependency} opened for {OpenDurationSeconds} seconds",
                dependency,
                15);
        }

        logger.LogError(lastFailure, "Call to {Dependency} failed after {Attempts} attempts", dependency, MaximumAttempts);
        throw new DependencyUnavailableException(dependency, "The dependency did not respond successfully.", lastFailure);
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}

public sealed class DependencyUnavailableException(
    string dependency,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Dependency { get; } = dependency;
}

public sealed class DependencyRejectedException(string dependency, HttpStatusCode statusCode)
    : Exception($"{dependency} rejected the request with {(int)statusCode}.")
{
    public string Dependency { get; } = dependency;
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class DependencyCircuitBreaker
{
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(15);
    private const int FailureThreshold = 3;
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _halfOpenProbeInProgress;

    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_openUntil is null)
            {
                return true;
            }

            if (_openUntil > DateTimeOffset.UtcNow || _halfOpenProbeInProgress)
            {
                return false;
            }

            _halfOpenProbeInProgress = true;
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
            _halfOpenProbeInProgress = false;
        }
    }

    public bool RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            var opened = _halfOpenProbeInProgress || _consecutiveFailures >= FailureThreshold;
            if (opened)
            {
                _openUntil = DateTimeOffset.UtcNow.Add(OpenDuration);
            }

            _halfOpenProbeInProgress = false;
            return opened;
        }
    }

    public void Abandon()
    {
        lock (_gate)
        {
            _halfOpenProbeInProgress = false;
        }
    }
}
