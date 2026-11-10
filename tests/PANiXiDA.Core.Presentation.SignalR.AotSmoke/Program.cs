using PANiXiDA.Core.Presentation.SignalR.AotSmoke;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSignalR(builder.Configuration).AddJsonProtocol(options =>
    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, SmokeJsonContext.Default));
builder.Services.AddApiDocumentationSchemas(SmokeJsonContext.Default);
await using var app = builder.Build();
app.UseSignalR(typeof(SmokeHub).Assembly);
var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();
RequestDelegate documentHandler = context => context.Response.WriteAsync(
    source.Generate("smoke-signalr-v1", context.RequestAborted).ToJsonString(), context.RequestAborted);
app.MapGet("/contract", documentHandler);
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address) };
var document = await client.GetStringAsync("/contract");
using var negotiate = await client.PostAsync("/hub/negotiate?negotiateVersion=1", null);
negotiate.EnsureSuccessStatusCode();
if (JsonNode.Parse(document)?["operations"]?.AsObject().Count != 1)
{
    throw new InvalidOperationException("Generated SignalR contracts were not available in Native AOT.");
}

Console.WriteLine(document);
await app.StopAsync();
