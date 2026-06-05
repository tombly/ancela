using Ancela.AppHost;
using Aspire.Hosting.Azure;
using Azure.Provisioning.ServiceBus;
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
var ownerPhoneNumberParameter = builder.AddParameter("owner-phone-number", true);
var twilioAccountSidParameter = builder.AddParameter("twilio-account-sid", true);
var twilioAuthTokenParameter = builder.AddParameter("twilio-auth-token", true);
var graphUserIdParameter = builder.AddParameter("graph-user-id", true);
var graphTenantIdParameter = builder.AddParameter("graph-tenant-id", true);
var graphClientIdParameter = builder.AddParameter("graph-client-id", true);
var graphClientSecretParameter = builder.AddParameter("graph-client-secret", true);
var ynabAccessToken = builder.AddParameter("ynab-access-token", true);
var tavilyApiKey = builder.AddParameter("tavily-api-key", true);
var remarkableDeviceToken = builder.AddParameter("remarkable-device-token", true);
// Required for access management: the owner's Base32 TOTP secret. invite/revoke require a
// current code and fail closed without this, so set it (the rest of the agent runs without it).
// Generate one with `ancela enroll` and scan the QR into an authenticator app.
var ownerTotpSecret = builder.AddParameter("owner-totp-secret", true);

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

var serviceBus = builder.AddAzureServiceBus("servicebus")
                        .RunAsEmulator()
                        .ConfigureInfrastructure(infrastructure =>
                        {
                            var ns = infrastructure.GetProvisionableResources()
                                                   .OfType<ServiceBusNamespace>()
                                                   .FirstOrDefault()
                                ?? throw new InvalidOperationException($"Could not find configured Service Bus namespace with name 'servicebus'");
                            ns.Sku = new ServiceBusSku
                            {
                                Name = ServiceBusSkuName.Standard,
                                Tier = ServiceBusSkuTier.Standard,
                            };
                        });
var remindersQueue = serviceBus.AddServiceBusQueue("reminders");
var standingRulesQueue = serviceBus.AddServiceBusQueue("standing-rules");
var scheduledTasksQueue = serviceBus.AddServiceBusQueue("scheduled-tasks");
var chatQueue = serviceBus.AddServiceBusQueue("chat-messages");

var functionApp = builder.AddAzureFunctionsProject<Projects.Ancela_FunctionApp>("functionapp")
    .WithReference(cosmosDb)
    .WithReference(blobs)
    .WithReference(serviceBus)
    .WithReference(chat)
    .WithHostStorage(storage)
    .WithEnvironment("TWILIO_PHONE_NUMBER", twilioPhoneNumberParameter)
    .WithEnvironment("OWNER_PHONE_NUMBER", ownerPhoneNumberParameter)
    .WithEnvironment("TWILIO_ACCOUNT_SID", twilioAccountSidParameter)
    .WithEnvironment("TWILIO_AUTH_TOKEN", twilioAuthTokenParameter)
    .WithEnvironment("GRAPH_USER_ID", graphUserIdParameter)
    .WithEnvironment("GRAPH_TENANT_ID", graphTenantIdParameter)
    .WithEnvironment("GRAPH_CLIENT_ID", graphClientIdParameter)
    .WithEnvironment("GRAPH_CLIENT_SECRET", graphClientSecretParameter)
    .WithEnvironment("YNAB_ACCESS_TOKEN", ynabAccessToken)
    .WithEnvironment("TAVILY_API_KEY", tavilyApiKey)
    .WithEnvironment("REMARKABLE_DEVICE_TOKEN", remarkableDeviceToken)
    .WithEnvironment("OWNER_TOTP_SECRET", ownerTotpSecret)
    .WithExternalHttpEndpoints();

builder.Build().Run();
