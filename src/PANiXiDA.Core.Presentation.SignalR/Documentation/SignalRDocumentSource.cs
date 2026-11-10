using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.AsyncApi;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

internal sealed class SignalRDocumentSource(
    MappedHubDocumentation mappings, ApiSchemaRegistry schemas, IServiceProvider services) : IAsyncApiDocumentSource
{
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
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = GetDocuments().SingleOrDefault(document => document.Name == documentName)
            ?? throw new ArgumentException($"SignalR document '{documentName}' was not found.", nameof(documentName));
        var operations = new List<AsyncApiOperation>();
        var serviceTypes = services.GetService<IServiceProviderIsService>();
        var endpoints = mappings.Endpoints?.Invoke() ?? services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var hub in mappings.Hubs.Where(hub => hub.Description.Document.Name == documentName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = hub.Description.Options(services);
            var endpoint = endpoints.OfType<RouteEndpoint>().FirstOrDefault(candidate =>
                candidate.Metadata.GetMetadata<HubMetadata>()?.HubType == hub.HubType && candidate.RoutePattern.RawText == hub.Path);
            var overrides = endpoint?.Metadata.GetOrderedMetadata<HubOperationOverride>() ?? [];
            ValidateOverrides(hub.Description, overrides);
            var context = new HubContext(hub.Path, IsAuthorized(endpoint),
                SupportedProtocols(options, hub.ConnectionOptions.Transports), !options.DisableImplicitFromServicesParameters, hub.ConnectionOptions.Transports);
            foreach (var method in hub.Description.Methods)
            {
                var parameters = method.Parameters.Where(parameter => IsSerialized(parameter, method, context, serviceTypes)).ToArray();
                var id = hub.HubType.FullName + "." + (method.IsClientMethod ? "client." : "hub.") + method.Name + "@" + hub.Path;
                var documentation = ResolveMetadata(hub.Description, method, overrides);
                operations.AddRange(CreateUploads(id, context, method, parameters, documentation));
                operations.Add(CreateInvocation(id, context, method, parameters, documentation));
            }
        }

        return AsyncApiDocumentBuilder.Build(descriptor, operations);
    }

    private static bool IsAuthorized(Endpoint? endpoint)
    {
        return endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
    }

    private static void ValidateOverrides(HubDescription hub, IReadOnlyList<HubOperationOverride> overrides)
    {
        foreach (var operation in overrides)
        {
            if (!hub.Methods.Any(method => method.MemberName == operation.MemberName && method.IsClientMethod == operation.IsClientMethod))
            {
                throw new InvalidOperationException($"Operation '{operation.MemberName}' does not identify a generated hub method or typed client callback.");
            }
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
            .Select(protocol => (JsonNode)JsonValue.Create(protocol.Name)!)]);
    }

    private static bool IsSerialized(HubParameterDescription parameter, HubMethodDescription method,
        HubContext context, IServiceProviderIsService? serviceTypes) => method.IsClientMethod || parameter.IsStream ||
        !context.ImplicitServices || !parameter.AllowImplicitServices || serviceTypes?.IsService(GetServiceType(parameter.Type)) != true;

    private IEnumerable<AsyncApiOperation> CreateUploads(string id, HubContext context, HubMethodDescription method,
        HubParameterDescription[] parameters, ApiOperationMetadata documentation)
    {
        return parameters.Where(parameter => parameter.IsStream).Select(parameter =>
        {
            var streamId = id + ".upload." + parameter.Name;
            return new AsyncApiOperation(streamId, context.Path, AsyncApiAction.Receive,
                new AsyncApiMessage(streamId, CreateReplySchema(parameter.Type, true))
                {
                    Title = documentation.Name + " / " + parameter.Name + " stream"
                }, context.WireProtocol)
            {
                Name = documentation.Name + " / " + parameter.Name,
                Bindings = new JsonObject
                {
                    ["x-signalr"] = new JsonObject
                    {
                        ["target"] = method.Name,
                        ["parameter"] = parameter.Name,
                        ["messageType"] = "streamItem",
                        ["authorizationRequired"] = context.Authorized || method.RequiresAuthorization,
                        ["transports"] = TransportNames(context.Transports),
                        ["transportProtocol"] = context.WireProtocol
                    }
                }
            };
        });
    }

    private AsyncApiOperation CreateInvocation(string id, HubContext context, HubMethodDescription method,
        HubParameterDescription[] parameters, ApiOperationMetadata documentation)
    {
        var streamNames = new JsonArray([.. parameters.Where(parameter => parameter.IsStream)
            .Select(parameter => (JsonNode)JsonValue.Create(parameter.Name)!)]);
        var binding = new JsonObject
        {
            ["target"] = method.Name,
            ["streamResult"] = method.IsStream,
            ["authorizationRequired"] = !method.IsClientMethod && (context.Authorized || method.RequiresAuthorization),
            ["inputStreams"] = streamNames,
            ["protocols"] = context.Protocols.DeepClone(),
            ["transports"] = TransportNames(context.Transports),
            ["transportProtocol"] = context.WireProtocol
        };
        var message = new AsyncApiMessage(id, InvocationSchema(method, parameters, streamNames.Count))
        {
            Title = documentation.Name + (method.IsClientMethod && method.ResultType is null ? " notification" : " request")
        };
        return new AsyncApiOperation(id, context.Path, method.IsClientMethod ? AsyncApiAction.Send : AsyncApiAction.Receive,
            message, context.WireProtocol, documentation.Summary)
        {
            Name = documentation.Name,
            Bindings = new JsonObject { ["x-signalr"] = binding },
            Reply = !method.IsClientMethod || method.ResultType is not null
                ? new AsyncApiMessage(id + ".result", CreateReplySchema(method.ResultType, method.IsStream))
                {
                    Title = documentation.Name + (method.IsStream ? " stream" : " response")
                } : null
        };
    }

    private JsonObject InvocationSchema(HubMethodDescription method, HubParameterDescription[] parameters, int streams)
    {
        var argumentSchemas = new JsonArray([.. parameters.Where(parameter => !parameter.IsStream)
            .Select((parameter, index) => schemas.GetSchema(parameter.Type, "#/properties/arguments/prefixItems/" + index))]);
        var arguments = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = argumentSchemas.Count,
            ["maxItems"] = argumentSchemas.Count
        };
        if (argumentSchemas.Count > 0)
        {
            arguments["prefixItems"] = argumentSchemas;
        }

        var properties = new JsonObject
        {
            ["type"] = new JsonObject { ["const"] = method.IsStream ? 4 : 1 },
            ["invocationId"] = new JsonObject { ["type"] = "string" },
            ["target"] = new JsonObject { ["const"] = method.Name },
            ["arguments"] = arguments
        };
        var required = new JsonArray("type", "target", "arguments");
        if (method.IsStream)
        {
            required.Add((JsonNode)JsonValue.Create("invocationId")!);
        }

        if (streams > 0)
        {
            properties["streamIds"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["minItems"] = streams,
                ["maxItems"] = streams
            };
            required.Add((JsonNode)JsonValue.Create("streamIds")!);
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static Type GetServiceType(Type type) => type.IsConstructedGenericType &&
        type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type.GenericTypeArguments[0] : type;

    private JsonObject CreateReplySchema(Type? resultType, bool stream)
    {
        var alternatives = new JsonArray();
        if (resultType is not null)
        {
            var property = stream ? "item" : "result";
            alternatives.Add((JsonNode)ProtocolMessage(stream ? 2 : 3, property,
                schemas.GetSchema(resultType, "#/oneOf/0/properties/" + property)));
        }

        if (resultType is null && !stream)
        {
            alternatives.Add((JsonNode)ProtocolMessage(3, "result", new JsonObject { ["type"] = "null" }));
        }

        if (stream)
        {
            alternatives.Add((JsonNode)ProtocolMessage(3));
        }

        alternatives.Add((JsonNode)ProtocolMessage(3, "error", new JsonObject { ["type"] = "string" }));
        return new JsonObject { ["oneOf"] = alternatives };
    }

    private static JsonObject ProtocolMessage(int messageType, string? member = null, JsonNode? schema = null)
    {
        var properties = new JsonObject
        {
            ["type"] = new JsonObject { ["const"] = messageType },
            ["invocationId"] = new JsonObject { ["type"] = "string" },
            ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }
        };
        var required = new JsonArray("type", "invocationId");
        if (member is not null)
        {
            properties[member] = schema;
            required.Add((JsonNode)JsonValue.Create(member)!);
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
            .Select(transport => (JsonNode)JsonValue.Create(transport.ToString())!)]);
    }

    private sealed record HubContext(string Path, bool Authorized, JsonArray Protocols, bool ImplicitServices, HttpTransportType Transports)
    {
        internal string WireProtocol => (Transports & HttpTransportType.WebSockets) != 0 ? "ws" : "http";
    }
}
