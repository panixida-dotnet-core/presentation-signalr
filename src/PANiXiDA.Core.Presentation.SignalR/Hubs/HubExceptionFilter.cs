using System.Reflection;

using Microsoft.AspNetCore.SignalR;

namespace PANiXiDA.Core.Presentation.SignalR.Hubs;

/// <summary>Preserves HubException handling when Native AOT reflection dispatch wraps a synchronous hub failure.</summary>
internal sealed class HubExceptionFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is HubException hubException)
        {
            return await ValueTask.FromException<object?>(hubException);
        }
    }
}
