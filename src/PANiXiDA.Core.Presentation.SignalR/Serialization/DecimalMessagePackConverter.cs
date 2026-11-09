using System.Globalization;
using System.Text.Json.Nodes;

using Nerdbank.MessagePack;

using PolyType;

namespace PANiXiDA.Core.Presentation.SignalR.Serialization;

/// <summary>Uses the invariant decimal string format expected by Microsoft's SignalR MessagePack client.</summary>
internal sealed class DecimalMessagePackConverter : MessagePackConverter<decimal>
{
    public override JsonObject GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
    {
        return new JsonObject { ["type"] = "string", ["description"] = "An invariant decimal string." };
    }

    public override decimal Read(ref MessagePackReader reader, SerializationContext context)
    {
        return decimal.Parse(reader.ReadString()!, CultureInfo.InvariantCulture);
    }

    public override void Write(ref MessagePackWriter writer, in decimal value, SerializationContext context)
    {
        writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }
}
