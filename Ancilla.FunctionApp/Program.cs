using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.ConfigureFunctionsWebApplication();
builder.AddAzureCosmosClient("cosmos");
builder.AddOpenAIClient(connectionName: "chat");

builder.Services.AddSingleton<Ancilla.FunctionApp.ChatService>();
builder.Services.AddSingleton<Ancilla.FunctionApp.SmsService>();
builder.Services.AddSingleton<Ancilla.FunctionApp.HistoryService>();

builder.Build().Run();