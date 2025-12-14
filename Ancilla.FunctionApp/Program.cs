using Ancilla.FunctionApp;
using Ancilla.FunctionApp.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.ConfigureFunctionsWebApplication();
builder.AddAzureCosmosClient("cosmos");
builder.AddOpenAIClient(connectionName: "chat");

builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<NoteService>();
builder.Services.AddSingleton<HistoryService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<ChatInterceptor>();

builder.Build().Run();