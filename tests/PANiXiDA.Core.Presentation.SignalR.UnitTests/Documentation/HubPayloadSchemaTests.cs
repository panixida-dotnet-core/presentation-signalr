using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using PANiXiDA.Core.Presentation.SignalR.Documentation;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Documentation;

public sealed class HubPayloadSchemaTests
{
    [Fact(DisplayName = "Generated payload descriptions reject missing factories and type lists")]
    public void RejectsInvalidArguments()
    {
        Should.Throw<ArgumentNullException>(() => new HubPayloadSchema(null!));
        Should.Throw<ArgumentNullException>(() => new HubPayloadSchema(_ => new JsonObject(), null!));
        Should.Throw<ArgumentException>(() => new HubPayloadSchema(_ => new JsonObject(), [null!]));
    }

    [Theory(DisplayName = "Changes to the JSON contract require an explicit schema context")]
    [InlineData(nameof(JsonSerializerOptions.IncludeFields))]
    [InlineData(nameof(JsonSerializerOptions.IgnoreReadOnlyProperties))]
    [InlineData(nameof(JsonSerializerOptions.DefaultIgnoreCondition))]
    [InlineData(nameof(JsonSerializerOptions.NumberHandling))]
    [InlineData(nameof(JsonSerializerOptions.ReferenceHandler))]
    [InlineData(nameof(JsonSerializerOptions.UnmappedMemberHandling))]
    [InlineData(nameof(JsonSerializerOptions.PreferredObjectCreationHandling))]
    [InlineData(nameof(JsonSerializerOptions.RespectNullableAnnotations))]
    [InlineData(nameof(JsonSerializerOptions.RespectRequiredConstructorParameters))]
    public void DefersToExplicitContextForCustomizedContracts(string setting)
    {
        var options = new JsonSerializerOptions
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        switch (setting)
        {
            case nameof(JsonSerializerOptions.IncludeFields):
                options.IncludeFields = true;
                break;
            case nameof(JsonSerializerOptions.IgnoreReadOnlyProperties):
                options.IgnoreReadOnlyProperties = true;
                break;
            case nameof(JsonSerializerOptions.DefaultIgnoreCondition):
                options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                break;
            case nameof(JsonSerializerOptions.NumberHandling):
                options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
                break;
            case nameof(JsonSerializerOptions.ReferenceHandler):
                options.ReferenceHandler = ReferenceHandler.Preserve;
                break;
            case nameof(JsonSerializerOptions.UnmappedMemberHandling):
                options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                break;
            case nameof(JsonSerializerOptions.PreferredObjectCreationHandling):
                options.PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate;
                break;
            case nameof(JsonSerializerOptions.RespectNullableAnnotations):
                options.RespectNullableAnnotations = false;
                break;
            case nameof(JsonSerializerOptions.RespectRequiredConstructorParameters):
                options.RespectRequiredConstructorParameters = false;
                break;
        }

        var schema = new HubPayloadSchema(_ => new JsonObject(), typeof(int));

        schema.Create(options).ShouldBeNull();
    }

    [Fact(DisplayName = "Legacy null omission requires explicit documentation metadata")]
    public void DefersToExplicitContextForLegacyNullOmission()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        typeof(JsonSerializerOptions).GetProperty("IgnoreNullValues")!.SetValue(options, true);
        options.MakeReadOnly();
        var schema = new HubPayloadSchema(_ => new JsonObject(), typeof(string));

        schema.Create(options).ShouldBeNull();
        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.Never);
    }

    [Theory(DisplayName = "Generated schemas require unmodified built-in JSON resolvers")]
    [InlineData("default", true)]
    [InlineData("modified", false)]
    [InlineData("derived", false)]
    public void ChecksConfiguredResolvers(string resolverKind, bool canGenerate)
    {
        var resolver = resolverKind == "derived" ? new DerivedResolver() : new DefaultJsonTypeInfoResolver();
        if (resolverKind == "modified")
        {
            resolver.Modifiers.Add(_ => { });
        }

        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        var schema = new HubPayloadSchema(_ => new JsonObject(), typeof(int));

        (schema.Create(options) is not null).ShouldBe(canGenerate);
    }

    private sealed class DerivedResolver : DefaultJsonTypeInfoResolver;
}
