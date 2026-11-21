using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.AsyncApi;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

internal sealed partial class SignalRDocumentSource(
    MappedHubDocumentation mappings, ApiSchemaRegistry schemas, IServiceProvider services,
    IOptions<SignalRDocumentationOptions> documentationOptions) : IAsyncApiDocumentSource
{
    private const string InvocationId = "invocationId";
    private const string Target = "target";
    private const string JsonString = "string";

    private readonly JsonSerializerContext[] _contexts = [.. services.GetServices<JsonSerializerContext>()];

    public IReadOnlyCollection<ApiDocumentDescriptor> GetDocuments()
    {
        return [.. mappings.Hubs.Select(hub => hub.Description.Document).GroupBy(document => document.Name).Select(ValidateDocument)];
    }

    private static ApiDocumentDescriptor ValidateDocument(IGrouping<string, ApiDocumentDescriptor> group)
    {
        var first = group.First();
        if (group.Any(document => document.Module != first.Module || document.Version != first.Version ||
            document.Title != first.Title || document.Deprecated != first.Deprecated))
        {
            throw new InvalidOperationException($"SignalR document '{first.Name}' has conflicting module metadata.");
        }

        return first;
    }

    public JsonObject Generate(string documentName, CancellationToken cancellationToken = default)
    {
        return GenerateDocumentAsync(documentName, cancellationToken).GetAwaiter().GetResult();
    }

    private async Task<JsonObject> GenerateDocumentAsync(string documentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = GetDocuments().SingleOrDefault(document => document.Name == documentName)
            ?? throw new ArgumentException($"SignalR document '{documentName}' was not found.", nameof(documentName));
        var operations = new List<AsyncApiOperation>();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var channelKeys = new HashSet<string>(StringComparer.Ordinal);
        var channels = new Dictionary<string, MappedHubDescription>(StringComparer.Ordinal);
        var serviceTypes = services.GetService<IServiceProviderIsService>();
        var endpoints = services.GetService<IHostApplicationLifetime>()?.ApplicationStarted.IsCancellationRequested == true
            ? services.GetRequiredService<EndpointDataSource>().Endpoints
            : mappings.Endpoints?.Invoke() ?? services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var hub in mappings.Hubs.Where(hub => hub.Description.Document.Name == documentName)
            .OrderBy(hub => hub.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = hub.Description.Options(services);
            var endpoint = endpoints.OfType<RouteEndpoint>().FirstOrDefault(candidate =>
                candidate.Metadata.GetMetadata<HubMetadata>()?.HubType == hub.HubType && candidate.RoutePattern.RawText == hub.Path);
            var overrides = endpoint?.Metadata.GetOrderedMetadata<HubOperationOverride>() ?? [];
            ValidateOverrides(hub.Description, overrides);
            var (authorized, requiresBearer) = await ResolveAuthorizationAsync(endpoint).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var channelKey = ReserveKey(channelKeys, VersionedRoutePrefix().Replace(hub.Path, ""), "channel");
            channels.Add(channelKey, hub);
            var context = new HubContext(channelKey, authorized, requiresBearer,
                SupportedProtocols(options, hub.ConnectionOptions.Transports), !options.DisableImplicitFromServicesParameters, hub.ConnectionOptions.Transports);
            foreach (var method in hub.Description.Methods)
            {
                var parameters = method.Parameters.Where(parameter => IsSerialized(parameter, method, context, serviceTypes)).ToArray();
                var id = ReserveKey(operationIds, method.Name, "operation");
                var documentation = ResolveMetadata(hub.Description, method, overrides);
                operations.AddRange(CreateUploads(operationIds, id, context, method, parameters, documentation));
                operations.Add(CreateInvocation(id, context, method, parameters, documentation));
            }
        }

        return BuildDocument(descriptor, operations, channels);
    }

    private static JsonObject BuildDocument(ApiDocumentDescriptor descriptor, IEnumerable<AsyncApiOperation> operations,
        Dictionary<string, MappedHubDescription> channels)
    {
        var document = AsyncApiDocumentBuilder.Build(descriptor, operations);
        foreach (var (key, channel) in document["channels"]!.AsObject())
        {
            var hub = channels[key];
            // The shared builder uses the supplied address as the key when creating channel references.
            channel!["address"] = hub.Path;
            if (hub.Description.ChannelTitle is { } title)
            {
                channel["title"] = title;
            }
        }
        return document;
    }

    private static string ReserveKey(HashSet<string> reservedKeys, string name, string fallback)
    {
        var slug = NonSlugCharacters().Replace(JsonNamingPolicy.KebabCaseLower.ConvertName(name), "-").Trim('-');
        var prefix = slug.Length == 0 ? fallback : slug[..Math.Min(slug.Length, 230)].TrimEnd('-');
        var id = prefix;
        var suffix = 2;
        while (!reservedKeys.Add(id))
        {
            id = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{suffix}");
            suffix++;
        }
        return id;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    [GeneratedRegex(@"^/(?:api/)?v[0-9]+(?:\.[0-9]+)*/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedRoutePrefix();

    private async Task<(bool Authorized, bool RequiresBearer)> ResolveAuthorizationAsync(Endpoint? endpoint)
    {
        var metadata = endpoint?.Metadata ?? EndpointMetadataCollection.Empty;
        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return (false, false);
        }

        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(provider, metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata.GetOrderedMetadata<AuthorizationPolicy>()).ConfigureAwait(false);
        if (policy is null)
        {
            return (false, false);
        }

        var bearer = metadata.GetMetadata<BearerDocumentation>() is not null ||
            await AcceptsBearerAsync(policy).ConfigureAwait(false);
        return (true, bearer);
    }

    private async Task<bool> AcceptsBearerAsync(AuthorizationPolicy policy)
    {
        if (policy.AuthenticationSchemes.Count > 0)
        {
            return policy.AuthenticationSchemes.Any(IsBearerScheme);
        }

        var provider = services.GetService<IAuthenticationSchemeProvider>();
        if (provider is null)
        {
            return false;
        }

        var scheme = await provider.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false);
        return scheme is not null && IsBearerScheme(scheme.Name);
    }

    private bool IsBearerScheme(string name)
    {
        return name is "Bearer" or BearerTokenDefaults.AuthenticationScheme or "OpenIddict.Validation.AspNetCore" ||
            documentationOptions.Value.BearerAuthenticationSchemes.Contains(name, StringComparer.Ordinal);
    }

    private static void ValidateOverrides(HubDescription hub, IReadOnlyList<HubOperationOverride> overrides)
    {
        var invalidOperation = overrides.FirstOrDefault(operation =>
            !hub.Methods.Any(method => method.MemberName == operation.MemberName && method.IsClientMethod == operation.IsClientMethod));
        if (invalidOperation is not null)
        {
            throw new InvalidOperationException($"Operation '{invalidOperation.MemberName}' does not identify a generated hub method or typed client callback.");
        }
    }

    private static ApiOperationMetadata ResolveMetadata(HubDescription hub, HubMethodDescription method,
        IReadOnlyList<HubOperationOverride> overrides)
    {
        var metadata = method.Documentation ?? new ApiOperationMetadata(method.MemberName, method.Summary);
        if (hub.Operations.TryGetValue((method.IsClientMethod ? "client." : string.Empty) + method.MemberName, out var configured))
        {
            metadata = new ApiOperationMetadata(configured.Name, configured.Summary ?? metadata.Summary);
        }

        foreach (var operation in overrides.Where(operation => operation.MemberName == method.MemberName && operation.IsClientMethod == method.IsClientMethod))
        {
            metadata = new ApiOperationMetadata(operation.Name ?? metadata.Name, operation.Summary ?? metadata.Summary);
        }

        return metadata;
    }

    private JsonArray SupportedProtocols(HubOptions options, HttpTransportType transports)
    {
        return new JsonArray([.. services.GetRequiredService<IHubProtocolResolver>().AllProtocols
            .Where(protocol => (options.SupportedProtocols is null || options.SupportedProtocols.Contains(protocol.Name, StringComparer.OrdinalIgnoreCase)) &&
                (protocol.TransferFormat != TransferFormat.Binary || (transports & (HttpTransportType.WebSockets | HttpTransportType.LongPolling)) != 0))
            .Select(protocol => (JsonNode?)JsonValue.Create(protocol.Name))]);
    }

    private static bool IsSerialized(HubParameterDescription parameter, HubMethodDescription method,
        HubContext context, IServiceProviderIsService? serviceTypes) => method.IsClientMethod || parameter.IsStream ||
        !context.ImplicitServices || !parameter.AllowImplicitServices || serviceTypes?.IsService(GetServiceType(parameter.Type)) != true;

    private IEnumerable<AsyncApiOperation> CreateUploads(HashSet<string> operationIds, string id, HubContext context, HubMethodDescription method,
        HubParameterDescription[] parameters, ApiOperationMetadata documentation)
    {
        return parameters.Where(parameter => parameter.IsStream).Select(parameter =>
        {
            var streamId = ReserveKey(operationIds, id + "-upload-" + parameter.Name, "operation");
            return new AsyncApiOperation(streamId, context.ChannelKey, AsyncApiAction.Receive,
                new AsyncApiMessage(streamId, CreateReplySchema(parameter.Type, parameter.Schema, true))
                {
                    Title = documentation.Name + " / " + parameter.Name + " stream"
                }, context.WireProtocol)
            {
                Name = documentation.Name + " / " + parameter.Name,
                RequiresBearer = context.RequiresBearer,
                Bindings = new JsonObject
                {
                    ["x-signalr"] = new JsonObject
                    {
                        [Target] = method.Name,
                        ["parameter"] = parameter.Name,
                        ["messageType"] = "streamItem",
                        ["authorizationRequired"] = context.Authorized || method.RequiresAuthorization,
                        ["transports"] = TransportNames(context.Transports)
                    }
                }
            };
        });
    }

    private AsyncApiOperation CreateInvocation(string id, HubContext context, HubMethodDescription method,
        HubParameterDescription[] parameters, ApiOperationMetadata documentation)
    {
        var streamNames = new JsonArray([.. parameters.Where(parameter => parameter.IsStream)
            .Select(parameter => (JsonNode?)JsonValue.Create(parameter.Name))]);
        var binding = new JsonObject
        {
            [Target] = method.Name,
            ["streamResult"] = method.IsStream,
            ["authorizationRequired"] = context.Authorized || (!method.IsClientMethod && method.RequiresAuthorization),
            ["inputStreams"] = streamNames,
            ["protocols"] = context.Protocols.DeepClone(),
            ["transports"] = TransportNames(context.Transports)
        };
        var message = new AsyncApiMessage(id, InvocationSchema(method, parameters, streamNames.Count))
        {
            Title = documentation.Name + (method.IsClientMethod && method.ResultType is null ? " notification" : " request")
        };
        var replySuffix = method.IsStream ? " stream" : " response";
        return new AsyncApiOperation(id, context.ChannelKey, method.IsClientMethod ? AsyncApiAction.Send : AsyncApiAction.Receive,
            message, context.WireProtocol, documentation.Summary)
        {
            Name = documentation.Name,
            RequiresBearer = context.RequiresBearer,
            Bindings = new JsonObject { ["x-signalr"] = binding },
            Reply = !method.IsClientMethod || method.ResultType is not null
                ? new AsyncApiMessage(id + ".result", CreateReplySchema(method.ResultType, method.ResultSchema, method.IsStream))
                {
                    Title = documentation.Name + replySuffix
                } : null
        };
    }

    private JsonObject InvocationSchema(HubMethodDescription method, HubParameterDescription[] parameters, int streams)
    {
        var serializedParameters = parameters.Where(parameter => !parameter.IsStream).ToArray();
        var arguments = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = serializedParameters.Length,
            ["maxItems"] = serializedParameters.Length
        };
        if (serializedParameters.Length == 1)
        {
            var parameter = serializedParameters[0];
            var name = GetArgumentTypeName(parameter.Type);
            var reference = "#/properties/arguments/$defs/" + name
                .Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            arguments["$defs"] = new JsonObject
            {
                [name] = GetSchema(parameter.Type, parameter.Schema, reference)
            };
            arguments["items"] = new JsonObject { ["$ref"] = reference };
        }
        else if (serializedParameters.Length > 1)
        {
            arguments["prefixItems"] = new JsonArray([.. serializedParameters.Select((parameter, index) =>
                GetSchema(parameter.Type, parameter.Schema, "#/properties/arguments/prefixItems/" + index))]);
        }

        var properties = new JsonObject
        {
            ["type"] = new JsonObject { ["const"] = method.IsStream ? 4 : 1 },
            [InvocationId] = new JsonObject { ["type"] = JsonString },
            [Target] = new JsonObject { ["const"] = method.Name },
            ["arguments"] = arguments
        };
        var required = new JsonArray("type", Target, "arguments");
        if (method.IsStream)
        {
            required.Add((JsonNode?)JsonValue.Create(InvocationId));
        }

        if (streams > 0)
        {
            properties["streamIds"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = JsonString },
                ["minItems"] = streams,
                ["maxItems"] = streams
            };
            required.Add((JsonNode?)JsonValue.Create("streamIds"));
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static string GetArgumentTypeName(Type type)
    {
        if (type.IsArray)
        {
            return GetArgumentTypeName(type.GetElementType()!) + "Array";
        }

        if (!type.IsConstructedGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var separator = name.IndexOf('`');
        if (separator >= 0)
        {
            name = name[..separator];
        }

        return name + "Of" + string.Join("And", type.GenericTypeArguments.Select(GetArgumentTypeName));
    }

    private static Type GetServiceType(Type type) => type.IsConstructedGenericType &&
        type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type.GenericTypeArguments[0] : type;

    private JsonNode GetSchema(Type type, HubPayloadSchema? generated, string referencePrefix)
    {
        if (generated is not null && !_contexts.Any(context => context.GetTypeInfo(type) is not null))
        {
            var options = services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
            if (generated.Create(options) is { } schema)
            {
                return schema;
            }
        }

        return schemas.GetSchema(type, referencePrefix);
    }

    private JsonObject CreateReplySchema(Type? resultType, HubPayloadSchema? generated, bool stream)
    {
        var alternatives = new JsonArray();
        if (resultType is not null)
        {
            var property = stream ? "item" : "result";
            alternatives.Add((JsonNode)ProtocolMessage(stream ? 2 : 3, property,
                GetSchema(resultType, generated, "#/oneOf/0/properties/" + property)));
        }

        if (resultType is null && !stream)
        {
            alternatives.Add((JsonNode)ProtocolMessage(3, "result", new JsonObject { ["type"] = "null" }));
        }

        if (stream)
        {
            alternatives.Add((JsonNode)ProtocolMessage(3));
        }

        alternatives.Add((JsonNode)ProtocolMessage(3, "error", new JsonObject { ["type"] = JsonString }));
        return new JsonObject { ["oneOf"] = alternatives };
    }

    private static JsonObject ProtocolMessage(int messageType, string? member = null, JsonNode? schema = null)
    {
        var properties = new JsonObject
        {
            ["type"] = new JsonObject { ["const"] = messageType },
            [InvocationId] = new JsonObject { ["type"] = JsonString },
            ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = JsonString } }
        };
        var required = new JsonArray("type", InvocationId);
        if (member is not null)
        {
            properties[member] = schema;
            required.Add((JsonNode?)JsonValue.Create(member));
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private static JsonArray TransportNames(HttpTransportType transports)
    {
        HttpTransportType[] candidates = [HttpTransportType.WebSockets, HttpTransportType.ServerSentEvents, HttpTransportType.LongPolling];
        return new JsonArray([.. candidates.Where(transport => (transports & transport) != 0)
            .Select(transport => (JsonNode?)JsonValue.Create(transport.ToString()))]);
    }

    private sealed record HubContext(string ChannelKey, bool Authorized, bool RequiresBearer, JsonArray Protocols,
        bool ImplicitServices, HttpTransportType Transports)
    {
        internal string WireProtocol => (Transports & HttpTransportType.WebSockets) != 0 ? "ws" : "http";
    }
}
