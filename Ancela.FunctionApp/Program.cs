using Ancela.Agent;
using Ancela.FunctionApp;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.ConfigureFunctionsWebApplication();
builder.AddAzureCosmosClient("cosmos");
builder.AddAzureBlobServiceClient("blobs");
builder.AddAzureServiceBusClient("servicebus");
builder.AddOpenAIClient(connectionName: "chat");
builder.AddAncelaAgent();

builder.Services.AddLogging(logging => logging.AddAncelaLogging());

builder.Build().Run();
