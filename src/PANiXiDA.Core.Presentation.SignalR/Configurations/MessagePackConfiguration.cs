using Microsoft.AspNetCore.SignalR;

using Nerdbank.MessagePack;
using Nerdbank.MessagePack.SignalR;

using PANiXiDA.Core.Presentation.SignalR.Serialization;

using PolyType;

namespace PANiXiDA.Core.Presentation.SignalR.Configurations;

internal static class MessagePackConfiguration
{
    internal static ISignalRServerBuilder AddMessagePackConfiguration(
        this ISignalRServerBuilder builder,
        ITypeShapeProvider types,
        MessagePackSerializer? serializer)
    {
        var defaults = new MessagePackSerializer
        {
            SerializeEnumValuesByName = true,
            Converters = [new DecimalMessagePackConverter(), new DateTimeOffsetMessagePackConverter()]
        }.WithGuidConverter(OptionalConverters.GuidStringFormat.StringD);
        return builder.AddMessagePackProtocol(types, serializer ?? defaults);
    }
}
