using System.Diagnostics;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace PANiXiDA.Core.Presentation.SignalR.Logging;

internal sealed class HubLoggingFilter(ILogger<HubLoggingFilter> logger) : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        return ExecuteAsync(invocationContext.Context, invocationContext.Hub, invocationContext.HubMethodName,
            () => next(invocationContext));
    }

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        return ExecuteAsync(context.Context, context.Hub, nameof(Hub.OnConnectedAsync), async () =>
        {
            await next(context);
            return null;
        }).AsTask();
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        return ExecuteAsync(context.Context, context.Hub, nameof(Hub.OnDisconnectedAsync), async () =>
        {
            await next(context, exception);
            return null;
        }, exception).AsTask();
    }

    private async ValueTask<object?> ExecuteAsync(
        HubCallerContext context,
        Hub hub,
        string method,
        Func<ValueTask<object?>> next,
        Exception? disconnectException = null)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = disconnectException is null ? "succeeded" : "failed";
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["rpc.system"] = "signalr",
            ["rpc.service"] = hub.GetType().FullName,
            ["rpc.method"] = method,
            ["signalr.connection.id"] = context.ConnectionId,
            ["enduser.id"] = context.UserIdentifier,
        }))
        {
            try
            {
                return await next();
            }
            catch (OperationCanceledException) when (context.ConnectionAborted.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "failed";
                throw;
            }
            finally
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    using (logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["signalr.operation.duration_ms"] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                        ["signalr.operation.outcome"] = outcome,
                    }))
                    {
                        logger.LogInformation("SignalR operation finished");
                    }
                }
            }
        }
    }
}
