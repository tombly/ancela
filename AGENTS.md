# AGENTS.md

This file provides guidance for AI coding agents working on the Ancela project.

## Project Overview

Ancela is an AI-powered memory assistant that helps users manage todos and knowledge through SMS messaging. It's built with:

- **.NET 10.0** with C# 13
- **.NET Aspire** for cloud-native orchestration
- **Azure Functions** (isolated worker model, v4)
- **Azure Cosmos DB** for data storage
- **Semantic Kernel** for AI/LLM integration
- **Twilio** for SMS messaging
- **OpenAI GPT models** for conversational AI

## Project Structure

```
Ancela/
├── Ancela.AppHost/          # .NET Aspire orchestration & infrastructure
├── Ancela.FunctionApp/      # Azure Functions HTTP endpoints (triggers & queue processors)
├── Ancela.Agent/            # Core agent logic, AI orchestration, and Semantic Kernel plugins
├── Ancela.Agent.Tests/      # Unit tests for agent functionality
├── Ancela.ServiceDefaults/  # Shared service configuration
└── *.slnx                    # Solution file
```

## Build & Run Commands

```bash
# Build the solution
dotnet build

# Run locally with Aspire (launches all services + dashboard)
dotnet run --project Ancela.AppHost

# Run tests (when available)
dotnet test

# Deploy to Azure
azd up
```

## Code Style & Conventions

- Use **file-scoped namespaces**
- Use **primary constructors** for dependency injection
- Use **raw string literals** (`"""`) for multi-line strings
- Prefer **async/await** patterns throughout
- Use **nullable reference types** (enabled project-wide)
- Follow standard C# naming conventions (PascalCase for public members, _camelCase for private fields)
- Use **4 spaces for indentation** (enforced via .editorconfig)
- If an interface has a single implementation then include the interface definition at the top of the implementation file.

## Key Components

### Ancela.AppHost
- `AppHost.cs` - Aspire distributed application builder, defines all Azure resources
- `FixedNameInfrastructureResolver.cs` - Custom naming for Azure resources
- `aspire-output/` - Generated Bicep templates for infrastructure

### Ancela.FunctionApp
- `Program.cs` - Function app startup and DI configuration
- `IncomingSms.cs` - HTTP trigger for incoming SMS messages from Twilio
- `IncomingMessage.cs` - HTTP trigger for testing (non-Twilio messages)
- `ChatQueueProcessor.cs` - Processes chat messages from Azure Queue Storage

### Ancela.Agent
- `Agent.cs` - Core AI agent with Semantic Kernel orchestration
- `ChatInterceptor.cs` - Handles special commands (`hello ancela`, `goodbye ancela`)
- `DependencyModule.cs` - Service registration for the agent module

#### Services (`Services/` folder)
- `SessionService.cs` - User session management in Cosmos DB
- `HistoryService.cs` - Conversation history persistence in Cosmos DB
- `SmsService.cs` - Twilio SMS integration

#### Semantic Kernel Plugins (`SemanticKernel/Plugins/` folder)
- `MemoryPlugin/` - Todo and knowledge management (CRUD operations)
  - `MemoryPlugin.cs` - Kernel functions for save/get/delete todos and knowledge
  - `MemoryClient.cs` - Cosmos DB operations for todos and knowledge
- `GraphPlugin/` - Microsoft Graph integration (calendar, email, contacts)
  - `GraphPlugin.cs` - Kernel functions for reading calendar, email, and contacts
  - `GraphClient.cs` - Microsoft Graph API client
- `YnabPlugin/` - YNAB (You Need A Budget) integration
  - `YnabPlugin.cs` - Kernel functions for reading budget and transaction data
  - `YnabClient.cs` - YNAB API client

### Ancela.Agent.Tests
- `AgentTestBase.cs` - Base class for agent tests
- `AgentTodoTests.cs` - Tests for todo functionality
- `AgentKnowledgeTests.cs` - Tests for knowledge functionality
- `AgentGraphTests.cs` - Tests for Graph integration

## Dependencies & Packages

Key NuGet packages:
- `Microsoft.Azure.Functions.Worker.*` - Azure Functions isolated worker
- `Aspire.Microsoft.Azure.Cosmos` - Aspire Cosmos DB integration
- `Aspire.OpenAI` - Aspire OpenAI integration
- `Microsoft.SemanticKernel` - AI orchestration framework
- `Twilio` - SMS messaging SDK

## Configuration

### Local Development
User secrets are required in `Ancela.AppHost`:
```bash
dotnet user-secrets set Parameters:openai-api-key "..."
dotnet user-secrets set Parameters:twilio-account-sid "..."
dotnet user-secrets set Parameters:twilio-auth-token "..."
dotnet user-secrets set Parameters:twilio-phone-number "..."
```

### Azure Deployment
- Secrets are prompted during `azd up`
- Cosmos DB uses role-based access (run `./configure_cosmos.sh` post-deployment)

## Cosmos DB Data Model

The app uses a single Cosmos DB database (`anceladb`) with containers for:
- **Sessions** - User session state (partitioned by `agentPhoneNumber`)
- **Todos** - User todo items (partitioned by `agentPhoneNumber`)
- **Knowledge** - Knowledge entries (partitioned by `agentPhoneNumber`)
- **History** - Conversation history (partitioned by conversation key: `{agentPhoneNumber}:{userPhoneNumber}`)

## Important Patterns

### Semantic Kernel Integration
- The `Agent` class builds a Kernel instance per request
- Plugins are registered via `kernel.Plugins.AddFromObject()` (MemoryPlugin, GraphPlugin, YnabPlugin)
- Function calling is enabled with `FunctionChoiceBehavior.Auto()`
- Context is passed via `kernel.Data[]` dictionary (agentPhoneNumber, userPhoneNumber)

### Session Flow
1. User texts "hello ancela" → `ChatInterceptor` creates new session
2. Subsequent messages → Routed to `ChatInterceptor` which delegates to `Agent` for AI processing
3. User texts "goodbye ancela" → `ChatInterceptor` ends session
4. Messages are processed asynchronously via Azure Queue Storage (`ChatQueueProcessor`)

## Testing Guidelines

When writing tests:
- Use xUnit for unit tests
- Mock external services (Cosmos DB, Twilio, OpenAI)
- Test services in isolation
- Integration tests should use Cosmos DB emulator

## Common Tasks

### Adding a New Service
1. Create service class in `Ancela.Agent/Services/`
2. Register in `DependencyModule.cs`
3. Inject via primary constructor where needed

### Adding a New Semantic Kernel Plugin
1. Create a new folder under `Ancela.Agent/SemanticKernel/Plugins/`
2. Add plugin class with `[KernelFunction]` and `[Description]` attributes
3. Add client class for external API/database operations
4. Register plugin in `DependencyModule.cs`
5. Add plugin to `Agent.cs` via `kernel.Plugins.AddFromObject()`
6. Update system instructions in `Agent.cs` if needed

### Adding a New AI Capability to Existing Plugin
1. Add method to the appropriate plugin class (e.g., `MemoryPlugin.cs`) with `[KernelFunction]` attribute
2. Update system instructions in `Agent.cs` if needed
3. Add corresponding tests in `Ancela.Agent.Tests/`

### Modifying Infrastructure
1. Update `AppHost.cs` with new resources
2. Re-run `dotnet run --project Ancela.AppHost` to regenerate Bicep in `aspire-output/`
3. Deploy with `azd up`
