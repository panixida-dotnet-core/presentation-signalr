using System.Reflection;
using System.Runtime.Loader;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Generators.Hubs;
using PANiXiDA.Core.Presentation.SignalR.Hubs;
using PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Generators;

public sealed class HubRegistrationGeneratorTests
{
    private static readonly string[] ReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    private const string BaseSource = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.SignalR;
        using PANiXiDA.Core.Presentation.SignalR.Hubs;
        public abstract class Base : Hub, IHubEndpoint
        {
            public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
                throw new System.NotSupportedException();
        }
        """;

    [Fact(DisplayName = "Generator skips compilations without the hub mapping contract")]
    public void Generate_ShouldSkipUnreferencedProjects()
    {
        var (result, _) = Compile("public class Unrelated { }", includeRuntime: false);

        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Generator emits an empty registration for assemblies without mapped hubs")]
    public void Generate_ShouldRegisterEmptyAssemblies()
    {
        var (result, _) = Compile("public class Unrelated { }");

        var generated = result.GeneratedTrees.Single().ToString();
        generated.ShouldContain("ModuleInitializer");
        generated.ShouldContain("HubRegistry.RegisterAssembly");
        generated.ShouldNotContain(".MapHub<");
    }

    [Fact(DisplayName = "Generator finds inherited and partial hub types in deterministic runtime-name order")]
    public void Generate_ShouldDiscoverAndOrderHubs()
    {
        var source = BaseSource + """
            public partial class AHub : Base { }
            public partial class AHub : Base { }
            public class ZHub : Base { }
            public class Container { internal class Nested : Base { } }
            public class ContainerAfter : Base { }
            public class UnmarkedHub : Hub { }
            public interface IUnused : IHubEndpoint { }
            namespace @event { public class @class : Base { } }
            """;

        var (result, _) = Compile(source);
        var generated = result.GeneratedTrees.Single().ToString();

        generated.Split(".MapHub<global::AHub>").Length.ShouldBe(2);
        generated.ShouldNotContain(".MapHub<global::Base>");
        generated.ShouldNotContain("UnmarkedHub");
        generated.ShouldNotContain("GetTypes(");
        generated.ShouldNotContain("Activator");
        generated.ShouldContain(".MapHub<global::@event.@class>");
        generated.IndexOf("global::AHub", StringComparison.Ordinal)
            .ShouldBeLessThan(generated.IndexOf("global::ZHub", StringComparison.Ordinal));
        generated.IndexOf("global::Container.Nested", StringComparison.Ordinal)
            .ShouldBeLessThan(generated.IndexOf("global::ContainerAfter", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Generator diagnoses inaccessible, generic and non-hub mapping types")]
    [InlineData("public class Container { private class Hidden : Base { } }", "PANSIGNALRSG001")]
    [InlineData("public class Container { protected class Hidden : Base { } }", "PANSIGNALRSG001")]
    [InlineData("file class Hidden : Base { }", "PANSIGNALRSG001")]
    [InlineData("public class Generic<T> : Base { }", "PANSIGNALRSG001")]
    [InlineData("public class Generic<T> { public class Nested : Base { } }", "PANSIGNALRSG001")]
    [InlineData("public class Invalid : IHubEndpoint { public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => throw new System.NotSupportedException(); }", "PANSIGNALRSG002")]
    [InlineData("public struct Invalid : IHubEndpoint { public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => throw new System.NotSupportedException(); }", "PANSIGNALRSG002")]
    public void Generate_ShouldDiagnoseUnsupportedTypes(string declaration, string expectedDiagnostic)
    {
        var (result, _) = Compile(BaseSource + declaration, expectedDiagnostic);

        result.Results.Single().Exception.ShouldBeNull();
    }

    [Fact(DisplayName = "Generated registration initializes an untouched assembly and supports explicit static contracts")]
    public void UseSignalR_ShouldMapGeneratedExplicitContract()
    {
        const string source = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            public sealed class ExternalHub : Hub, IHubEndpoint
            {
                static HubEndpointConventionBuilder IHubEndpoint.Map(HubMapBuilder hubs) =>
                    hubs.MapHub<ExternalHub>("/external").RequireAuthorization("ExternalPolicy");
            }
            """;
        var (_, compilation) = Compile(source);
        var context = new AssemblyLoadContext("generated-hub-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = Load(compilation, context);
            using var app = CreateApplication();

            app.UseSignalR(assembly).ShouldBeSameAs(app);

            var endpoint = HubEndpoints(app).ShouldHaveSingleItem();
            endpoint.RoutePattern.RawText.ShouldBe("/external");
            endpoint.Metadata.GetMetadata<IAuthorizeData>()!.Policy.ShouldBe("ExternalPolicy");
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact(DisplayName = "Assembly mapping preserves assembly order and maps duplicate assemblies only once")]
    public void UseSignalR_ShouldMapMultipleAssembliesAndIgnoreDuplicates()
    {
        var (_, compilation) = Compile("""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            public class ExternalHub : Hub, IHubEndpoint
            {
                public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
                    hubs.MapHub<ExternalHub>("/external");
            }
            """);
        var context = new AssemblyLoadContext("generated-hubs-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var external = Load(compilation, context);
            var local = typeof(SignalRHostingTests).Assembly;
            using var app = CreateApplication();

            app.UseSignalR(external, local, local);

            HubEndpoints(app).Select(endpoint => endpoint.RoutePattern.RawText)
                .ShouldBe(["/external", "/constructed", "/converter", "/v1/state", "/v2/state", "/generated", "/metadata", "/secondary",
                    "/hubs/other", "/hubs/events"]);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact(DisplayName = "Assembly mapping rejects assemblies without a generated registration")]
    public void UseSignalR_ShouldRejectMissingRegistration()
    {
        var (_, compilation) = Compile("public class Unrelated { }", includeRuntime: false);
        var context = new AssemblyLoadContext("unregistered-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = Load(compilation, context);
            using var app = CreateApplication();

            var exception = Should.Throw<InvalidOperationException>(() => app.UseSignalR(assembly));

            exception.Message.ShouldContain("Generated hub registration was not found");
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory(DisplayName = "Assembly mapping rejects empty assembly lists and assemblies without mapped hubs")]
    [InlineData(false)]
    [InlineData(true)]
    public void UseSignalR_ShouldRejectEmptyMappings(bool includeEmptyAssembly)
    {
        using var app = CreateApplication();
        Assembly[] assemblies = includeEmptyAssembly ? [typeof(HubRegistry).Assembly] : [];

        var exception = Should.Throw<ArgumentException>(() => app.UseSignalR(assemblies));

        exception.ParamName.ShouldBe("assemblies");
    }

    [Fact(DisplayName = "Generated assembly registrations cannot be overwritten")]
    public void RegisterAssembly_ShouldRejectDuplicates()
    {
        Should.Throw<ArgumentException>(() => HubRegistry.RegisterAssembly(typeof(SignalRHostingTests).Assembly, _ => { }));
    }

    [Fact(DisplayName = "Null mapping callbacks are rejected without preventing a subsequent valid registration")]
    public void RegisterAssembly_ShouldRejectNullCallbackWithoutRegisteringAssembly()
    {
        var (_, compilation) = Compile("public class Unrelated { }", includeRuntime: false);
        var context = new AssemblyLoadContext("registration-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = Load(compilation, context);
            using var app = CreateApplication();

            var exception = Should.Throw<ArgumentNullException>(() => HubRegistry.RegisterAssembly(assembly, null!));

            exception.ParamName.ShouldBe("mapHubs");
            HubRegistry.RegisterAssembly(assembly, hubs => hubs.MapHub<SignalRHostingTests.OtherHub>("/registered"));
            app.UseSignalR(assembly);
            HubEndpoints(app).ShouldHaveSingleItem().RoutePattern.RawText.ShouldBe("/registered");
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory(DisplayName = "Documentation handles async return shapes inherited attributes callbacks arrays and dynamic payloads")]
    [InlineData("object")]
    [InlineData("string[]")]
    public void Generate_ShouldCoverStandardContractShapes(string untypedClient)
    {
        var source = $$"""
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            using System.Threading.Tasks;
            using System.Threading.Channels;
            using System.Collections.Generic;
            public sealed class Sequence : IAsyncEnumerable<string> { public IAsyncEnumerator<string> GetAsyncEnumerator(System.Threading.CancellationToken token = default) => null!; }
            public abstract class DerivedReader : ChannelReader<string> { }
            public interface IClient { Task Changed([Microsoft.AspNetCore.Mvc.FromServices] string state, System.Threading.CancellationToken cancellationToken); Task<int> Prompt(); Task NonTrailing(System.Threading.CancellationToken token, int value); }
            public abstract class Parent : Hub<IClient>
            {
                [Microsoft.AspNetCore.Authorization.Authorize]
                [HubMethodName("inherited")]
                public virtual Task<string> Read() => Task.FromResult("state");
            }
            public class Documented : Parent, IDocumentedHubEndpoint
            {
                {{ContractMembers("Documented")}}
                /// <remarks>Method documentation without a summary.</remarks>
                public override Task<string> Read() => base.Read();
                public ValueTask Nothing() => ValueTask.CompletedTask;
                public ValueTask<string> Value() => new("value");
                public Task<int[]> Array() => Task.FromResult(new[] { 1 });
                public ChannelReader<string> Watch(System.Threading.CancellationToken cancellationToken) => Channel.CreateUnbounded<string>().Reader;
                public IAsyncEnumerable<string> Updates() => null!;
                public Task Upload(ChannelReader<string> items) => Task.CompletedTask;
                public void ReadKeyed([Microsoft.Extensions.DependencyInjection.FromKeyedServices("key")] object service) { }
                public void Decorated([System.Runtime.InteropServices.Optional] string value) { }
                public dynamic Echo(dynamic value) => value;
                public List<string> Values(List<string> values) => values;
                public Sequence CustomStream() => new();
                public Task<DerivedReader> DerivedStream() => Task.FromResult<DerivedReader>(null!);
                public void TokenAsData(System.Threading.CancellationToken token, int value) { }
                public int Dispose(int value) => value;
                protected void Hidden() { }
                public static void Static() { }
                public string Property { get; set; } = "";
            }
            public class UntypedClient : Hub<{{untypedClient}}>, IDocumentedHubEndpoint { {{ContractMembers("UntypedClient", "other")}} public void Run() { } }
            """;
        var (result, _) = Compile(source);
        var generated = result.GeneratedTrees.Single().ToString();
        generated.ShouldContain("\"inherited\"");
        generated.ShouldContain("requiresAuthorization: true");
        generated.ShouldContain("typeof(global::System.Object)");
        generated.ShouldNotContain("typeof(global::Sequence)");
        generated.ShouldNotContain("typeof(global::DerivedReader)");
        generated.ShouldNotContain("HubParameterDescription(\"service\"");
        generated.ShouldNotContain("\"Hidden\"");
        generated.ShouldNotContain("\"Static\"");
    }

    [Fact(DisplayName = "Inherited client disposal callbacks are documented while hub disposal stays excluded")]
    public void Generate_ShouldPreserveClientDispose()
    {
        var source = $$"""
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            public interface IBaseClient { System.Threading.Tasks.Task Dispose(); }
            public interface IClient : IBaseClient { }
            public class Documented : Hub<IClient>, IDocumentedHubEndpoint
            {
                {{ContractMembers("Documented")}}
                public new void Dispose() => base.Dispose();
            }
            """;

        var (result, _) = Compile(source);

        var generated = result.GeneratedTrees.Single().ToString();
        generated.ShouldContain("MemberName = \"Dispose\"");
        generated.ShouldContain("isClientMethod: true");
        generated.ShouldNotContain("isClientMethod: false");
    }

    [Fact(DisplayName = "Overrides with different nullable annotations are documented once")]
    public void Generate_ShouldIgnoreNullableAnnotationsInOverrides()
    {
        var source = $$"""
            using System.Collections.Generic;
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            public abstract class Parent : Hub
            {
                public virtual void Send(List<string> value) { }
            }
            public class Documented : Parent, IDocumentedHubEndpoint
            {
                {{ContractMembers("Documented")}}
                public override void Send(List<string?>? value) { }
            }
            """;

        var (result, _) = Compile(source);

        result.GeneratedTrees.Single().ToString().Split("MemberName = \"Send\"").Length.ShouldBe(2);
    }

    [Theory(DisplayName = "Documented typed callbacks reject unsupported streaming signatures")]
    [InlineData("System.Threading.Tasks.Task Watch(System.Collections.Generic.IAsyncEnumerable<string> values);")]
    [InlineData("System.Collections.Generic.IAsyncEnumerable<string> Watch();")]
    public void Generate_ShouldRejectStreamingClientCallbacks(string method)
    {
        Compile("public interface IClient { " + method + " } " + DocumentedSource(string.Empty, "Microsoft.AspNetCore.SignalR.Hub<IClient>"), "PANSIGNALRSG003");
    }

    [Fact(DisplayName = "Awaiter metadata resolves inherited awaitables and preserves ordinary payload types")]
    public void Generate_ShouldDistinguishAwaitablesFromPayloads()
    {
        var source = $$"""
            using System;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            public class AwaitableBase
            {
                public TaskAwaiter<int> GetAwaiter() => Task.FromResult(42).GetAwaiter();
            }
            public class Awaitable : AwaitableBase { }
            public class PayloadWithOverloads
            {
                public TaskAwaiter<int> GetAwaiter(int value) => Task.FromResult(value).GetAwaiter();
                public static TaskAwaiter<int> GetAwaiter() => Task.FromResult(42).GetAwaiter();
                public TaskAwaiter<int> GetAwaiter<T>() => Task.FromResult(42).GetAwaiter();
            }
            public class PayloadWithoutNotification
            {
                public object GetAwaiter() => new object();
            }
            public class PayloadWithoutCompletionFlag
            {
                public Awaiter GetAwaiter() => new Awaiter();
                public class Awaiter : INotifyCompletion
                {
                    public int IsCompleted => 1;
                    public int GetResult() => 42;
                    public void OnCompleted(Action continuation) => continuation();
                }
            }
            public class PayloadWithoutResult
            {
                public Awaiter GetAwaiter() => new Awaiter();
                public class Awaiter : INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void OnCompleted(Action continuation) => continuation();
                }
            }
            public class Documented : Hub, IDocumentedHubEndpoint
            {
                {{ContractMembers("Documented")}}
                public Awaitable Read() => new Awaitable();
                public PayloadWithOverloads Overloads() => new PayloadWithOverloads();
                public PayloadWithoutNotification NoNotification() => new PayloadWithoutNotification();
                public PayloadWithoutCompletionFlag NoCompletionFlag() => new PayloadWithoutCompletionFlag();
                public PayloadWithoutResult NoResult() => new PayloadWithoutResult();
            }
            """;

        var (result, _) = Compile(source);

        var generated = result.GeneratedTrees.Single().ToString();
        generated.ShouldContain("typeof(int)");
        generated.ShouldNotContain("typeof(global::Awaitable)");
        generated.ShouldContain("typeof(global::PayloadWithOverloads)");
        generated.ShouldContain("typeof(global::PayloadWithoutNotification)");
        generated.ShouldContain("typeof(global::PayloadWithoutCompletionFlag)");
        generated.ShouldContain("typeof(global::PayloadWithoutResult)");
    }

    [Fact(DisplayName = "Generator tolerates incomplete attributes without replacing compiler diagnostics")]
    public void Generate_ShouldTolerateIncompleteCode()
    {
        var (result, _) = Compile("""
            public class Incomplete : Microsoft.AspNetCore.SignalR.Hub, PANiXiDA.Core.Presentation.SignalR.Hubs.IDocumentedHubEndpoint { }
            public class Documented : Microsoft.AspNetCore.SignalR.Hub, PANiXiDA.Core.Presentation.SignalR.Hubs.IDocumentedHubEndpoint
            {
                [Unknown]
                public void Run([Unknown] string value) { }
            }
            """, allowCompilationErrors: true);
        result.Results.Single().Exception.ShouldBeNull();
    }
    private static WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        return builder.Build();
    }

    [Fact(DisplayName = "Documentation generator reads interface contracts inherited callbacks summaries and explicit service parameters")]
    public void Generate_ShouldDescribeDocumentedContracts()
    {
        var source = $$"""
            using Microsoft.AspNetCore.SignalR;
            using PANiXiDA.Core.Presentation.SignalR.Hubs;
            public interface IBaseClient { System.Threading.Tasks.Task Changed(string state); }
            public interface IClient : IBaseClient { }
            public class Documented : Hub<IClient>, IDocumentedHubEndpoint
            {
                {{ContractMembers("Documented", "matches", 2)}}
                /// <summary>Reads the current state.</summary>
                [HubMethodName("read")]
                public System.Threading.Tasks.ValueTask<string> Read(
                    string id, [Microsoft.AspNetCore.Mvc.FromServices] System.IDisposable service) => new(id);
                public override System.Threading.Tasks.Task OnConnectedAsync() => base.OnConnectedAsync();
                public override string ToString() => "hub";
                private string Hidden() => "hidden";
            }
            """;
        var (result, _) = Compile(source);
        var generated = result.GeneratedTrees.Single().ToString();
        generated.ShouldContain("HubDocumentationRegistry.RegisterHub<global::Documented>");
        generated.ShouldContain("MemberName = \"Read\"");
        generated.ShouldContain("\"read\"");
        generated.ShouldContain("isClientMethod: true");
        generated.ShouldContain("\"Changed\"");
        generated.ShouldNotContain("HubParameterDescription(\"service\"");
        generated.ShouldNotContain("\"OnConnectedAsync\"");
        generated.ShouldNotContain("\"ToString\"");
        generated.ShouldNotContain("GetMethods(");
    }

    [Theory(DisplayName = "Documented hubs report unsupported generic methods and overloaded targets at compile time")]
    [InlineData("public void Read<T>(T value) { }")]
    [InlineData("public void Read(ref int value) { }")]
    [InlineData("public unsafe void Read(int* value) { }")]
    [InlineData("public unsafe void Read(delegate*<void> value) { }")]
    [InlineData("public void Read(System.Span<int> value) { }")]
    [InlineData("public System.ReadOnlySpan<int> Read() => default;")]
    [InlineData("private int value; public ref int Read() => ref value;")]
    [InlineData("private int value; public ref readonly int Read() => ref value;")]
    [InlineData("public void Read(string value) { } public void Read(int value) { }")]
    public void Generate_ShouldDiagnoseUnsupportedDocumentedMethods(string methods)
    {
        Compile(DocumentedSource(methods), "PANSIGNALRSG003");
    }

    [Theory(DisplayName = "Standard endpoint attributes generate display metadata independently of wire names")]
    [InlineData("\"Readable operation\"", "\"Readable description\"", "Readable operation", "Readable description")]
    [InlineData("null", "null", "Read", "XML fallback")]
    public void Generate_ShouldReadEndpointAttributes(string name, string summary, string expectedName, string expectedSummary)
    {
        var (result, _) = Compile(DocumentedSource($$"""
            /// <summary>XML fallback</summary>
            [Microsoft.AspNetCore.SignalR.HubMethodName("wire-read")]
            [Microsoft.AspNetCore.Routing.EndpointName({{name}})]
            [Microsoft.AspNetCore.Http.EndpointSummary({{summary}})]
            public string Read() => "state";
            """));

        var generated = result.GeneratedTrees.Single().ToString();
        generated.ShouldContain("HubMethodDescription(\"wire-read\"");
        generated.ShouldContain("MemberName = \"Read\"");
        generated.ShouldContain($"ApiOperationMetadata(\"{expectedName}\", \"{expectedSummary}\")");
    }

    [Fact(DisplayName = "Empty endpoint names produce a compile-time diagnostic")]
    public void Generate_ShouldRejectEmptyDisplayNames()
    {
        Compile(DocumentedSource("""[Microsoft.AspNetCore.Routing.EndpointName(" ")] public void Read() { }"""),
            "PANSIGNALRSG004");
    }

    [Fact(DisplayName = "The generator emits schemas for ordinary DTOs and their serialized members")]
    public void EmitsSchemasForOrdinaryPayloads()
    {
        const string declarations = """
            using System;
            using System.Collections.Generic;
            using System.Diagnostics.CodeAnalysis;
            using System.Text.Json.Serialization;

            public sealed record Payload(Guid? Id, DateTimeOffset Date, bool Enabled, byte[] Data, string[] Tags,
                IReadOnlyList<int?> Values, Dictionary<string, Member> Members, PayloadKind Kind, Member Field,
                MemberWithConstructors Other, NamedMember? Named, Box<Box<Initialized>> Nested);

            public sealed record Box<T>(T Value);

            public sealed class Initialized
            {
                [SetsRequiredMembers]
                public Initialized(int count, string? note, int readOnly, [AllowNull] string hint, [DisallowNull] string? key)
                {
                    Count = count;
                    Note = note;
                    ReadOnly = readOnly;
                    Hint = hint ?? "default";
                    Key = key;
                    Name = "default";
                }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                public int Count { get; }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                public string? Note { get; }

                public string Hint { get; }

                public string? Key { get; }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
                public int ReadOnly { get; }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
                public string? Presentation { get; init; }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
                public string? WriteOnly { get; init; }

                public required string Name { get; init; }
            }

            public enum PayloadKind { A, B }

            public struct Member
            {
                public double Count { get; set; }
            }

            public class MemberWithConstructors
            {
                public MemberWithConstructors() { }
                public MemberWithConstructors(string? value) { Value = value; }
                public string? Value { get; }
            }

            public class BaseMember
            {
                public virtual string Visible => "";
                public virtual string Hidden => "";
            }

            public sealed class NamedMember : BaseMember
            {
                [JsonConstructor]
                public NamedMember(int count, string? note = null) { Count = count; Note = note; }

                public static string Static => "";
                public string this[int index] => "";
                private string Private => "";
                public string SetOnly { set { } }

                public required string Name { get; init; }
                [JsonRequired]
                public bool Selected { get; init; }
                [JsonPropertyName("member_count")]
                public int Count { get; }
                public string? Note { get; }

                [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
                public override string Visible => "";
                [JsonIgnore]
                public override string Hidden => "";
            }
            """;
        var (result, _) = Compile(declarations + DocumentedSource("public Payload Echo(Payload input) => input;"));

        var source = result.GeneratedTrees.Single().GetText(TestContext.Current.CancellationToken).ToString();
        source.ShouldContain("ResultSchema = new");
        source.ShouldContain("Schema = new");
        source.ShouldContain("\"member_count\"");
        source.ShouldContain("\"additionalProperties\"");
        source.ShouldContain("\"uuid\"");
        source.ShouldNotContain("\"Hidden\"");
    }

    [Fact(DisplayName = "The generator defers custom and unsupported JSON contracts to explicit contexts")]
    public void OmitsSchemasForContractsThatNeedExplicitMetadata()
    {
        const string declarations = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;

            [JsonConverter(typeof(JsonStringEnumConverter<CustomKind>))]
            public enum CustomKind { A }

            public sealed record ExpandingPayload<T>(ExpandingPayload<List<T>>? Next);

            public interface InterfacePayload { string Value { get; } }
            public abstract class AbstractPayload;
            public sealed class RecursivePayload { public RecursivePayload? Next { get; init; } }
            public sealed class Sequence : IEnumerable<string>
            {
                public IEnumerator<string> GetEnumerator() => throw new NotImplementedException();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public sealed class IncludedField { [JsonInclude] public int Value; }
            public sealed class IncludedProperty { [JsonInclude] private int Value { get; set; } }
            public sealed class ExtensionPayload { [JsonExtensionData] public Dictionary<string, JsonElement>? Values { get; init; } }
            public sealed class UntypedMember { public object? Value { get; init; } }
            public class BasePayload { public string? Value { get; init; } }
            public sealed class ShadowedProperty : BasePayload
            {
                [JsonPropertyName("other")]
                public new string? Value { get; init; }
            }
            [JsonPolymorphic]
            public class PolymorphicBase;
            public sealed class PolymorphicChild : PolymorphicBase;
            """;
        var source = declarations + DocumentedSource("""
            public CustomKind? Custom() => null;
            public InterfacePayload Interface() => null!;
            public AbstractPayload Abstract() => null!;
            public RecursivePayload Recursive() => null!;
            public ExpandingPayload<int> Expanding() => null!;
            public Sequence Enumerable() => null!;
            public object Untyped() => null!;
            public System.Threading.CancellationToken Framework() => default;
            public int[,] Matrix() => null!;
            public object[] UntypedItems() => [];
            public Dictionary<int, string> NonStringKeys() => [];
            public Dictionary<string, object> UntypedValues() => [];
            public IncludedField Field() => new();
            public IncludedProperty Property() => new();
            public ExtensionPayload Extension() => new();
            public UntypedMember Unknown() => new();
            public ShadowedProperty Shadowed() => new();
            public PolymorphicChild Polymorphic() => new();
            """);

        var (result, _) = Compile(source);

        result.GeneratedTrees.Single().GetText(TestContext.Current.CancellationToken).ToString().ShouldNotContain("ResultSchema =");
    }

    [Fact(DisplayName = "An unbound JSON attribute does not interrupt hub generation")]
    public void KeepsGeneratingWhenJsonAttributeDoesNotBind()
    {
        const string declaration = """
            public sealed class Payload
            {
                [System.Text.Json.Serialization.JsonPropertyName]
                public string Value { get; init; } = "";
            }
            """;

        var (result, _) = Compile(declaration + DocumentedSource("public Payload Read() => new();"), allowCompilationErrors: true);

        result.GeneratedTrees.Single().GetText(TestContext.Current.CancellationToken).ToString().ShouldContain("ConvertName(\"Value\")");
    }

    private static string DocumentedSource(string methods, string baseType = "Microsoft.AspNetCore.SignalR.Hub") => $$"""
        public class Documented : {{baseType}}, PANiXiDA.Core.Presentation.SignalR.Hubs.IDocumentedHubEndpoint
        {
            {{ContractMembers("Documented")}}
            {{methods}}
        }
        """;

    private static string ContractMembers(string typeName, string module = "module", int major = 1) => $$"""
        public static string Name => "{{module}}";
        public static string Summary => "{{module}}";
        public static Asp.Versioning.ApiVersion ApiVersion => new({{major}}, 0);
        public static Microsoft.AspNetCore.Builder.HubEndpointConventionBuilder Map(PANiXiDA.Core.Presentation.SignalR.Hubs.HubMapBuilder hubs) => hubs.MapHub<{{typeName}}>("/generated");
        """;

    private static RouteEndpoint[] HubEndpoints(WebApplication app)
    {
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HubMetadata>() is not null
                && !endpoint.RoutePattern.RawText!.EndsWith("/negotiate", StringComparison.Ordinal))];
    }

    private static Assembly Load(Compilation compilation, AssemblyLoadContext context)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        result.Success.ShouldBeTrue();
        stream.Position = 0;
        return context.LoadFromStream(stream);
    }

    private static (GeneratorDriverRunResult Result, Compilation Compilation) Compile(
        string source,
        string? expectedDiagnostic = null,
        bool includeRuntime = true, bool allowCompilationErrors = false)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var references = ReferencePaths
            .Where(path => includeRuntime || path != typeof(IHubEndpoint).Assembly.Location)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GeneratedHubs_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: TestContext.Current.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
        var driver = CSharpGeneratorDriver.Create([new HubRegistrationGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);
        diagnostics.Select(diagnostic => diagnostic.Id).ShouldBe(expectedDiagnostic is null ? [] : [expectedDiagnostic]);
        if (!allowCompilationErrors)
        {
            updated.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        }
        return (driver.GetRunResult(), updated);
    }
}
