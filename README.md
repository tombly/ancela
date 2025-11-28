# ancilla
AI memory assistant

# Notes

After deploying to Azure, your Entra principal must be granted the Contributor role for the Cosmos DB resource to view the Data Explorer:
``` bash
az cosmosdb sql role assignment create --account-name "<COSMOS>" --resource-group "ancilla" --scope "/" --principal-id "<GUID>" --role-definition-name "Cosmos DB Built-in Data Contributor"
```
