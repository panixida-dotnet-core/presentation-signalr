using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.AsyncApi;

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
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var hub in mappings.Hubs.Where(hub => hub.Description.Document.Name == documentName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = hub.Description.Options(services);
            var context = new HubContext(hub.Path, IsAuthorized(hub.HubType, hub.Path, endpoints),
                SupportedProtocols(options), !options.DisableImplicitFromServicesParameters);
            foreach (var method in hub.Description.Methods)
            {
                var parameters = method.Parameters.Where(parameter => IsSerialized(parameter, method, context, serviceTypes)).ToArray();
                var id = hub.HubType.FullName + "." + (method.IsClientMethod ? "client." : "hub.") + method.Name + "@" + hub.Path;
                operations.AddRange(CreateUploads(id, hub.Path, method, parameters));
                operations.Add(CreateInvocation(id, context, method, parameters));
            }
        }

        return AsyncApiDocumentBuilder.Build(descriptor, operations);
    }

    private static bool IsAuthorized(Type hubType, string path, IReadOnlyList<Endpoint> endpoints)
    {
        var endpoint = endpoints.OfType<RouteEndpoint>().FirstOrDefault(endpoint =>
            endpoint.Metadata.GetMetadata<HubMetadata>()?.HubType == hubType && endpoint.RoutePattern.RawText == path);
        return endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
    }

    private JsonArray SupportedProtocols(HubOptions options)
    {
        return new JsonArray([.. services.GetRequiredService<IHubProtocolResolver>().AllProtocols
            .Where(protocol => options.SupportedProtocols is null || options.SupportedProtocols.Contains(protocol.Name, StringComparer.OrdinalIgnoreCase))
            .Select(protocol => (JsonNode)JsonValue.Create(protocol.Name)!)]);
    }

    private static bool IsSerialized(HubParameterDescription parameter, HubMethodDescription method,
        HubContext context, IServiceProviderIsService? serviceTypes) => method.IsClientMethod || parameter.IsStream ||
        !context.ImplicitServices || !parameter.AllowImplicitServices || serviceTypes?.IsService(GetServiceType(parameter.Type)) != true;

    private IEnumerable<AsyncApiOperation> CreateUploads(string id, string path, HubMethodDescription method,
        HubParameterDescription[] parameters)
    {
        return parameters.Where(parameter => parameter.IsStream).Select(parameter =>
        {
            var streamId = id + ".upload." + parameter.Name;
            return new AsyncApiOperation(streamId, path, AsyncApiAction.Receive,
                new AsyncApiMessage(streamId, CreateReplySchema(parameter.Type, true)), "ws")
            {
                Bindings = new JsonObject
                {
                    ["x-signalr"] = new JsonObject
                    {
                        ["target"] = method.Name,
                        ["parameter"] = parameter.Name,
                        ["messageType"] = "streamItem"
                    }
                }
            };
        });
    }

    private AsyncApiOperation CreateInvocation(string id, HubContext context, HubMethodDescription method,
        HubParameterDescription[] parameters)
    {
        var streamNames = new JsonArray([.. parameters.Where(parameter => parameter.IsStream)
            .Select(parameter => (JsonNode)JsonValue.Create(parameter.Name)!)]);
        var binding = new JsonObject
        {
            ["target"] = method.Name,
            ["streamResult"] = method.IsStream,
            ["authorizationRequired"] = !method.IsClientMethod && (context.Authorized || method.RequiresAuthorization),
            ["inputStreams"] = streamNames,
            ["protocols"] = context.Protocols.DeepClone()
        };
        return new AsyncApiOperation(id, context.Path, method.IsClientMethod ? AsyncApiAction.Send : AsyncApiAction.Receive,
            new AsyncApiMessage(id, InvocationSchema(method, parameters, streamNames.Count)), "ws", method.Summary)
        {
            Bindings = new JsonObject { ["x-signalr"] = binding },
            Reply = !method.IsClientMethod || method.ResultType is not null
                ? new AsyncApiMessage(id + ".result", CreateReplySchema(method.ResultType, method.IsStream)) : null
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

        if (stream || resultType is null)
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

    private sealed record HubContext(string Path, bool Authorized, JsonArray Protocols, bool ImplicitServices);
}
