using Nerdbank.MessagePack;

using PANiXiDA.Core.Presentation.SignalR.Configurations;
using PANiXiDA.Core.Presentation.SignalR.Serialization;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Serialization;

public sealed class MessagePackConverterSchemaTests
{
    [Fact(DisplayName = "Decimal contracts document their invariant string encoding")]
    public void GetJsonSchema_ShouldDescribeDecimal()
    {
        var serializer = new MessagePackSerializer { Converters = [new DecimalMessagePackConverter()] };
        var shape = MessagePackTypes.GeneratedTypeShapeProvider.GetTypeShape(typeof(decimal))!;

        var document = serializer.GetJsonSchema(shape);
        var schema = document["$defs"]![typeof(decimal).FullName!]!;

        schema["type"]!.GetValue<string>().ShouldBe("string");
    }

    [Fact(DisplayName = "Date offset contracts document a timestamp and a bounded offset tuple")]
    public void GetJsonSchema_ShouldDescribeDateOffset()
    {
        var serializer = new MessagePackSerializer { Converters = [new DateTimeOffsetMessagePackConverter()] };
        var shape = MessagePackTypes.GeneratedTypeShapeProvider.GetTypeShape(typeof(DateTimeOffset))!;

        var document = serializer.GetJsonSchema(shape);
        var schema = document["$defs"]![typeof(DateTimeOffset).FullName!]!;

        schema["type"]!.GetValue<string>().ShouldBe("array");
        schema["minItems"]!.GetValue<int>().ShouldBe(2);
        schema["maxItems"]!.GetValue<int>().ShouldBe(2);
        var items = schema["prefixItems"]!.AsArray();
        items.Count.ShouldBe(2);
        items[1]!["type"]!.GetValue<string>().ShouldBe("integer");
        items[1]!["minimum"]!.GetValue<int>().ShouldBe(-840);
        items[1]!["maximum"]!.GetValue<int>().ShouldBe(840);
    }
}
