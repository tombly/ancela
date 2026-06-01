
resource_group="${ANCELA_RESOURCE_GROUP:?ANCELA_RESOURCE_GROUP is not set}"
cosmos_account="${ANCELA_RESOURCE_PREFIX:?ANCELA_RESOURCE_PREFIX is not set}-cosmos"

echo "Configuring $resource_group/$cosmos_account..."

principal_id=$(az ad signed-in-user show --query id -o tsv)

az cosmosdb sql role assignment create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --scope "/" \
  --principal-id $principal_id \
  --role-definition-name "Cosmos DB Built-in Data Contributor"

az cosmosdb sql database create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --name "anceladb"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "todos" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "knowledge" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "history" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "users" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "reminders" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "standing_rules" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "scheduled_tasks" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "projects" \
  --partition-key-path "/agentPhoneNumber"

az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "audit" \
  --partition-key-path "/agentPhoneNumber"
