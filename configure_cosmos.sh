
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

# Audit entries expire after 90 days (Cosmos deletes an item that long after its last write).
# They hold tool arguments and results, such as an email's text, and nothing reads further back:
# check_anomalies looks at most 30 days back.
az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "audit" \
  --partition-key-path "/agentPhoneNumber" \
  --ttl 7776000

# Holds the single owner OAuth token per provider (e.g. google-health). Deliberately omitted from the
# read-only CLI catalog so the audit viewer can't dump access/refresh tokens.
az cosmosdb sql container create \
  --account-name $cosmos_account \
  --resource-group $resource_group \
  --database-name "anceladb" \
  --name "oauth_tokens" \
  --partition-key-path "/provider"
