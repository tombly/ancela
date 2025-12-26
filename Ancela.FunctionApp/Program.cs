using Ancela.Agent;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using Ancela.Agent.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.ConfigureFunctionsWebApplication();
builder.AddAzureCosmosClient("cosmos");
builder.AddOpenAIClient(connectionName: "chat");

builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<IGraphClient, GraphClient>();
builder.Services.AddSingleton<IHistoryService, HistoryService>();
builder.Services.AddSingleton<ITodoService, TodoService>();
builder.Services.AddSingleton<IKnowledgeService, KnowledgeService>();
builder.Services.AddSingleton<ISessionService, SessionService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddSingleton<CommandInterceptor>();

builder.Build().Run();