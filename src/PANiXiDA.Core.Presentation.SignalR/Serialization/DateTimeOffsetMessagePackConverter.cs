using System.Text.Json.Nodes;

using Nerdbank.MessagePack;

using PolyType;

namespace PANiXiDA.Core.Presentation.SignalR.Serialization;

/// <summary>Preserves the wall-clock timestamp and offset used by Microsoft's SignalR MessagePack client.</summary>
internal sealed class DateTimeOffsetMessagePackConverter : MessagePackConverter<DateTimeOffset>
{
    public override JsonObject GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
    {
        var schema = context.CreateTupleSchema(new JsonArray(CreateMsgPackExtensionSchema(-1),
            new JsonObject { ["type"] = "integer", ["minimum"] = -840, ["maximum"] = 840 }));
        schema["minItems"] = 2;
        schema["maxItems"] = 2;
        return schema;
    }

    public override DateTimeOffset Read(ref MessagePackReader reader, SerializationContext context)
    {
        if (reader.ReadArrayHeader() != 2)
        {
            throw new MessagePackSerializationException("A DateTimeOffset requires a timestamp and an offset.");
        }

        var timestamp = reader.ReadDateTime();
        return new DateTimeOffset(timestamp.Ticks, TimeSpan.FromMinutes(reader.ReadInt16()));
    }

    public override void Write(ref MessagePackWriter writer, in DateTimeOffset value, SerializationContext context)
    {
        writer.WriteArrayHeader(2);
        writer.Write(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Utc));
        writer.Write((short)value.Offset.TotalMinutes);
    }
}
