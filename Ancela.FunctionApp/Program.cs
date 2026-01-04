using Ancela.Agent;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.ConfigureFunctionsWebApplication();
builder.AddAzureCosmosClient("cosmos");
builder.AddAzureQueueServiceClient("queues");
builder.AddOpenAIClient(connectionName: "chat");
builder.AddAncelaAgent();

builder.Build().Run();
