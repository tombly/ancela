# Ancilla

**AI Memory Assistant via SMS**

Ancilla is an AI-powered memory assistant that helps users save and retrieve notes through SMS messaging. Built with .NET Aspire, Azure Functions, and Azure Cosmos DB, it provides an intelligent, conversational interface for managing personal notes and information.

## Features

- 💬 **SMS Interface** - Interact with your AI assistant via text messages using Twilio
- 🧠 **AI-Powered Conversations** - Powered by OpenAI GPT models with Semantic Kernel
- 📝 **Note Management** - Save, retrieve, and delete notes using natural language
- 🔐 **Session Management** - Secure, per-user sessions with simple commands
- 📜 **Conversation History** - Maintains context across multiple interactions
- 🌍 **Timezone Support** - Automatic timezone handling for accurate timestamps
- ☁️ **Cloud Native** - Built with .NET Aspire for easy deployment to Azure
- 🗄️ **Scalable Storage** - Azure Cosmos DB for reliable, distributed data storage

## Architecture

Ancilla is built using modern cloud-native patterns:

- **Ancilla.AppHost** - .NET Aspire orchestration and infrastructure
- **Ancilla.FunctionApp** - Azure Functions for serverless HTTP endpoints
- **Ancilla.ServiceDefaults** - Shared service configuration and defaults

### Key Components

- **CommandInterceptor** - Handles special commands (`hello ancilla`, `goodbye ancilla`)
- **ChatService** - AI conversation management with OpenAI integration
- **NoteService** - CRUD operations for user notes in Cosmos DB
- **SessionService** - User session lifecycle management
- **HistoryService** - Conversation history persistence and retrieval
- **SmsService** - Twilio integration for SMS messaging

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- [Azure subscription](https://azure.microsoft.com/free/) (for deployment)
- [Twilio account](https://www.twilio.com/) (for SMS functionality)
- [OpenAI API key](https://platform.openai.com/) or Azure OpenAI service

## Getting Started

### Local Development

1. **Clone the repository**
   ```bash
   git clone https://github.com/tombly/ancilla.git
   cd ancilla
   ```

2. **Configure user secrets**
   
   Navigate to the AppHost project and set your secrets:
   ```bash
   cd Ancilla.AppHost
   dotnet user-secrets set Parameters:openai-api-key "your-openai-api-key"
   dotnet user-secrets set Parameters:twilio-account-sid "your-twilio-account-sid"
   dotnet user-secrets set Parameters:twilio-auth-token "your-twilio-auth-token"
   dotnet user-secrets set Parameters:twilio-phone-number "your-twilio-phone-number"
   ```

3. **Update the resource group name**
   
   Edit `FixedNameInfrastructureResolver.cs` to set your Azure resource group name.

4. **Run locally**
   ```bash
   dotnet run --project Ancilla.AppHost
   ```

   The Aspire dashboard will launch, showing all running services and their endpoints.

### Deployment to Azure

1. **Configure infrastructure**
   
   The project uses .NET Aspire for infrastructure-as-code. Bicep templates are generated in `Ancilla.AppHost/aspire-output/`.

2. **Deploy using Aspire**
   ```bash
   azd init
   azd up
   ```

   Aspire will prompt you for required secrets during deployment.

3. **Post-deployment configuration**

   After deploying, grant your Entra principal the Contributor role for Cosmos DB to access Data Explorer:
   ```bash
   # Run the configure_cosmos.sh script or manually assign roles in Azure Portal
   ./configure_cosmos.sh
   ```

4. **Create Cosmos DB containers**
   
   Note: The managed identity doesn't have permission to create databases/containers in Azure. Use the provided script:
   ```bash
   # Script coming soon - manually create via Azure Portal for now
   # Required containers: sessions, notes, history
   ```

## Usage

### Starting a Session

Send an SMS to your Twilio number:
```
hello ancilla
```

Response:
```
Welcome! I'm your AI memory assistant. I can help you save and retrieve notes via SMS. Try sending me a note!
```

### Saving Notes

Simply send natural language messages:
```
Remember that my dentist appointment is next Tuesday at 2pm
```

```
Save a note: buy milk, eggs, and bread
```

### Retrieving Notes

Ask for your notes in natural language:
```
What notes do I have?
```

```
List my notes
```

```
Do I have any appointments?
```

### Deleting Notes

Request deletion naturally:
```
Delete the note about the dentist
```

```
Remove the grocery list
```

### Ending a Session

When you're done:
```
goodbye ancilla
```

Response:
```
Goodbye! Your session has been ended. Your notes have been preserved. Send 'hello ancilla' to start a new session.
```

## Configuration

### Environment Variables

The following environment variables are used (configured via user secrets in development):

- `Parameters:openai-api-key` - Your OpenAI API key
- `Parameters:twilio-account-sid` - Twilio account SID
- `Parameters:twilio-auth-token` - Twilio auth token
- `Parameters:twilio-phone-number` - Your Twilio phone number

### View Current Secrets

```bash
cd Ancilla.AppHost
dotnet user-secrets list | grep Parameters
```

## Project Structure

```
Ancilla/
├── Ancilla.AppHost/           # Aspire orchestration
│   ├── AppHost.cs             # Service definitions
│   ├── aspire-output/         # Generated Bicep templates
│   └── FixedNameInfrastructureResolver.cs
├── Ancilla.FunctionApp/       # Azure Functions app
│   ├── CommandInterceptor.cs # Command handling
│   ├── CosmosPlugin.cs        # Semantic Kernel plugin
│   ├── IncomingMessage.cs     # HTTP message endpoint
│   ├── IncomingSms.cs         # Twilio webhook endpoint
│   └── Services/              # Business logic services
│       ├── ChatService.cs
│       ├── HistoryService.cs
│       ├── NoteService.cs
│       ├── SessionService.cs
│       └── SmsService.cs
└── Ancilla.ServiceDefaults/   # Shared configuration
```

## Data Model

### Sessions
- Partitioned by `agentPhoneNumber`
- Tracks active user sessions
- Stores user timezone preferences

### Notes
- Partitioned by `agentPhoneNumber`
- Associated with `userPhoneNumber`
- Supports soft deletion
- Preserved when sessions end

### History
- Partitioned by `agentPhoneNumber`
- Maintains conversation context
- One entry per user message and AI response

## Technologies

- [.NET 10](https://dotnet.microsoft.com/)
- [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/)
- [Azure Functions](https://azure.microsoft.com/services/functions/)
- [Azure Cosmos DB](https://azure.microsoft.com/services/cosmos-db/)
- [Semantic Kernel](https://github.com/microsoft/semantic-kernel)
- [OpenAI GPT](https://platform.openai.com/)
- [Twilio](https://www.twilio.com/)

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Built with [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) for cloud-native development
- Powered by [Semantic Kernel](https://github.com/microsoft/semantic-kernel) for AI orchestration
- SMS integration via [Twilio](https://www.twilio.com/)

## Support

For questions or issues, please [open an issue](https://github.com/tombly/ancilla/issues) on GitHub.

---

**Made with ❤️ using .NET Aspire**
