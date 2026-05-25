using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Roles;
using Azure.Provisioning.ServiceBus;
using Azure.Provisioning.Storage;

namespace Ancela.AppHost;

public sealed class FixedNameInfrastructureResolver() : InfrastructureResolver
{
    private static readonly string Prefix =
        Environment.GetEnvironmentVariable("ANCELA_RESOURCE_PREFIX")
        ?? throw new InvalidOperationException("ANCELA_RESOURCE_PREFIX environment variable is not set.");

    // For resources that require alphanumeric-only names (no hyphens).
    private static readonly string PrefixNoHyphens = Prefix.Replace("-", string.Empty);

    public override void ResolveProperties(ProvisionableConstruct construct, ProvisioningBuildOptions options)
    {
        switch (construct)
        {
            case CosmosDBAccount cosmosAccount:
                cosmosAccount.Name = $"{Prefix}-cosmos";
                break;

            case StorageAccount storageAccount:
                // Storage account names are alphanumeric only, max 24 chars.
                storageAccount.Name = $"{PrefixNoHyphens}storage";
                break;

            case ServiceBusNamespace serviceBusNamespace:
                serviceBusNamespace.Name = $"{Prefix}-servicebus";
                break;

            case ContainerRegistryService containerRegistry:
                // Container registry names are alphanumeric only, max 50 chars, globally unique.
                containerRegistry.Name = $"{PrefixNoHyphens}acr";
                break;

            case OperationalInsightsWorkspace workspace:
                workspace.Name = $"{Prefix}-law";
                break;

            case UserAssignedIdentity identity when identity.BicepIdentifier == "env_mi":
                identity.Name = $"{Prefix}-identity-env";
                break;

            case UserAssignedIdentity identity when identity.BicepIdentifier == "functionapp_identity":
                identity.Name = $"{Prefix}-identity-functionapp";
                break;

            case ContainerAppManagedEnvironment containerAppEnvironment:
                containerAppEnvironment.Name = $"{Prefix}-environment";
                break;

            case ContainerApp containerApp:
                containerApp.Name = $"{Prefix}-functionapp";
                break;

            case KeyVaultService keyVault:
                keyVault.Name = $"{Prefix}-keyvault";
                break;

            default:
                break;
        }
    }
}
