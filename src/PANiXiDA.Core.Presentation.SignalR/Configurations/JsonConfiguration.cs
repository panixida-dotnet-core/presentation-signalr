using System.Text.Json.Serialization;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace PANiXiDA.Core.Presentation.SignalR.Configurations;

internal static class JsonConfiguration
{
    internal static ISignalRServerBuilder AddJsonConfiguration(this ISignalRServerBuilder builder)
    {
        return builder.AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            options.PayloadSerializerOptions.RespectRequiredConstructorParameters = true;
            options.PayloadSerializerOptions.RespectNullableAnnotations = true;
        });
    }
}
