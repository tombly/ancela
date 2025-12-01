# Ancilla
AI memory assistant

# Notes

After deploying to Azure, your Entra principal must be granted the Contributor role for the Cosmos DB resource to view the Data Explorer:
``` bash
az cosmosdb sql role assignment create --account-name "<COSMOS>" --resource-group "ancilla" --scope "/" --principal-id "<GUID>" --role-definition-name "Cosmos DB Built-in Data Contributor"
```

Add each secret parameter to your local user secrets, for example:
```bash
dotnet user-secrets set Parameters:openai-api-key sk...
```

When deploying, Aspire will prompt you for each secret. You can view your local secrets by running the following command from Ancilla.AppHost folder:
```bash
dotnet user-secrets list | grep Parameters
```
