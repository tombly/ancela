using Ancilla.AppHost;
using Aspire.Hosting.Azure;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.Configure<AzureProvisioningOptions>(options =>
{
    options.ProvisioningBuildOptions.InfrastructureResolvers.Insert(0, new FixedNameInfrastructureResolver());
});

builder.AddAzureContainerAppEnvironment("env");

var openAiApiKeyParameter = builder.AddParameter("openai-api-key", true);
var twilioPhoneNumberParameter = builder.AddParameter("twilio-phone-number", true);
var twilioAccountSidParameter = builder.AddParameter("twilio-account-sid", true);
var twilioAuthTokenParameter = builder.AddParameter("twilio-auth-token", true);
var graphUserIdParameter = builder.AddParameter("graph-user-id", true);
var graphTenantIdParameter = builder.AddParameter("graph-tenant-id", true);
var graphClientIdParameter = builder.AddParameter("graph-client-id", true);
var graphClientSecretParameter = builder.AddParameter("graph-client-secret", true);

var openai = builder.AddOpenAI("openai").WithApiKey(openAiApiKeyParameter);
var chat = openai.AddModel("chat", "gpt-5-mini");

var cosmosDb = builder.AddAzureCosmosDB("cosmos")
                      .RunAsPreviewEmulator(configureContainer: container =>
                      {
                          container.WithDataExplorer();
                      });

var storage = builder.AddAzureStorage("storage")
                     .RunAsEmulator()
                     .ConfigureInfrastructure((infrastructure) =>
                     {
                         var storageAccount = infrastructure.GetProvisionableResources()
                                                            .OfType<StorageAccount>()
                                                            .FirstOrDefault(r => r.BicepIdentifier == "storage")
                             ?? throw new InvalidOperationException($"Could not find configured storage account with name 'storage'");
                         storageAccount.AllowBlobPublicAccess = false;
                     });

var blobs = storage.AddBlobs("blobs");
var queues = storage.AddQueues("queues");
var tables = storage.AddTables("tables");

var functionApp = builder.AddAzureFunctionsProject<Projects.Ancilla_FunctionApp>("functionapp")
    .WithReference(cosmosDb)
    .WithReference(blobs)
    .WithReference(queues)
    .WithReference(tables)
    .WithReference(chat)
    .WithHostStorage(storage)
    .WithEnvironment("TWILIO_PHONE_NUMBER", twilioPhoneNumberParameter)
    .WithEnvironment("TWILIO_ACCOUNT_SID", twilioAccountSidParameter)
    .WithEnvironment("TWILIO_AUTH_TOKEN", twilioAuthTokenParameter)
    .WithEnvironment("GRAPH_USER_ID", graphUserIdParameter)
    .WithEnvironment("GRAPH_TENANT_ID", graphTenantIdParameter)
    .WithEnvironment("GRAPH_CLIENT_ID", graphClientIdParameter)
    .WithEnvironment("GRAPH_CLIENT_SECRET", graphClientSecretParameter)
    .WithExternalHttpEndpoints();

builder.Build().Run();