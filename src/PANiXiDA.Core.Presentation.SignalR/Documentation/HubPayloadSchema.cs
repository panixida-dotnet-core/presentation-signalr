using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

/// <summary>Describes a payload schema emitted by the hub generator.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class HubPayloadSchema
{
    private readonly Func<JsonNamingPolicy?, JsonNode> _create;
    private readonly Type[] _types;

    /// <summary>Creates a schema from a factory and the types used by its serialization contract.</summary>
    /// <param name="create">The factory for a fresh schema using the hub's property naming policy.</param>
    /// <param name="types">The payload type and its serialized member types.</param>
    public HubPayloadSchema(Func<JsonNamingPolicy?, JsonNode> create, params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(types);
        if (types.Any(type => type is null))
        {
            throw new ArgumentException("Schema types must not contain null.", nameof(types));
        }

        _create = create;
        _types = [.. types];
    }

    internal JsonNode? Create(JsonSerializerOptions options)
    {
        var standardContract = options is
        {
            IncludeFields: false,
            IgnoreReadOnlyProperties: false,
            DefaultIgnoreCondition: JsonIgnoreCondition.Never,
            NumberHandling: JsonNumberHandling.Strict,
            ReferenceHandler: null,
            UnmappedMemberHandling: JsonUnmappedMemberHandling.Skip,
            PreferredObjectCreationHandling: JsonObjectCreationHandling.Replace,
            RespectNullableAnnotations: true,
            RespectRequiredConstructorParameters: true
        };

        return standardContract && !IgnoresNullValues(options) && options.TypeInfoResolverChain.All(IsStandardResolver) &&
            !options.Converters.Any(converter => _types.Any(converter.CanConvert))
            ? _create(options.PropertyNamingPolicy)
            : null;
    }

    private static bool IgnoresNullValues(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        try
        {
            // The legacy IgnoreNullValues setting prevents setting a default ignore condition.
            copy.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool IsStandardResolver(IJsonTypeInfoResolver resolver) =>
        resolver is DefaultJsonTypeInfoResolver defaultResolver &&
        defaultResolver.GetType() == typeof(DefaultJsonTypeInfoResolver) && defaultResolver.Modifiers.Count == 0;
}
