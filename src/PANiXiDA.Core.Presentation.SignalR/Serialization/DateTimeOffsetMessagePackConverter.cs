using Nerdbank.MessagePack;

namespace PANiXiDA.Core.Presentation.SignalR.Serialization;

/// <summary>Preserves the wall-clock timestamp and offset used by Microsoft's SignalR MessagePack client.</summary>
internal sealed class DateTimeOffsetMessagePackConverter : MessagePackConverter<DateTimeOffset>
{
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
