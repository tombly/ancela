# Ancela

Ancela is an experimental AI assistant. The goal of this project is to create an assistant that is actually useful by integrating with real-world services, supplementing them with special AI abilities, and possessing limited autonomy. 

## How It Works

Ancela is built using modern cloud-native patterns: .NET Aspire for infrastructure-as-code, Azure Functions for serverless compute, Azure Cosmos DB for scalable storage, and Semantic Kernel for AI orchestration. The assistant interacts with users via SMS using Twilio, and leverages OpenAI GPT models to understand and respond to natural language requests.

Ancela's capabilities include:
- Managing todos
- Accessing calendar events (read and create)
- Reading and sending emails
- Searching contacts
- Storing persistent knowledge
- Accessing personal finances
- Sending SMS messages
- Searching and fetching web content
- Scheduling one-time SMS reminders
- Watching standing rules (recurring conditions that notify when met)
- Running scheduled tasks (recurring actions that report back on a clock schedule)

![Design](Images/design.svg)

## Design & trust model

Ancela is a **single-owner** assistant. Each deployed instance is wired to exactly
one owner's accounts — one Microsoft Graph identity (`GRAPH_USER_ID`: mail, calendar,
contacts) and one YNAB token — using app-only credentials held by the instance.

The owner authorizes a **small, trusted set of people** (by phone number) to talk to
the agent over SMS. Those users act *through* the agent and can therefore reach the
owner's connected data — read/send the owner's mail, read/write the calendar, read
contacts and finances. **This is intentional.** Authorized users are trusted
principals invited by the owner; they are not mutually-isolated tenants, and the
agent is not a multi-tenant SaaS. Reviewers should treat cross-user access to the
owner's data as by-design, not as a vulnerability.

Data model implications:
- All Cosmos containers partition on `/agentPhoneNumber` (one value per instance).
- **Knowledge and to-dos are shared** across the instance's authorized users — it is
  one shared memory, by design.
- **Chat history is per-user** (filtered by `userPhoneNumber`).
- Reminders, standing rules, and scheduled tasks are created and listed per-user.

Access boundary: only the owner self-registers; everyone else must be `invite`d by
the owner (by phone number) before `hello ancela` does anything, and the owner can
`revoke` them. Identity, though, still rests on the SMS sender number, which is
spoofable — so the highest-value privilege (the owner's own `invite`/`revoke`) is the
weakest point if someone spoofs the owner's number.

**Owner step-up (TOTP), required.** `invite`/`revoke` require a second factor: the
owner appends a current 6-digit authenticator code (e.g. `invite +15551234567 408291`),
verified server-side via RFC 6238. This binds access-management to *possession of the
secret*, not just the claimed number. The gate **fails closed**: `OWNER_TOTP_SECRET`
must be set, and if it is missing (or malformed) access-management is refused rather
than falling back to number-only — it cannot be bypassed by leaving the secret unset.
Run `ancela enroll` to mint a secret and scan its QR into an authenticator app, then
set `OWNER_TOTP_SECRET`. (Self-registration and normal chat are unaffected, so a fresh
instance still runs; only `invite`/`revoke` are gated. If the owner loses their
authenticator, re-run `ancela enroll` and reset the secret.) Caveat — the code travels
in the SMS body, which Twilio and the carriers can see, so this defends against number
spoofing, not against an attacker who can read the owner's texts or the application
logs; it is lightweight hardening, not hardware MFA.

What is *not* in the trust model: untrusted **web content** fetched by the agent
(`web_search` / `web_fetch`) is not trusted, and neither is any external page reached
during autonomous standing-rule or scheduled-task evaluation. That content must never
be treated as instructions.

**Autonomous profiles.** Two kernel profiles run without a human in the loop:
`StandingRule` (condition evaluated on a timer) and `ScheduledTask` (action run on a
clock schedule). In both cases the model acts from a queue trigger with no user
present to review an action before it happens, so these profiles remove the *ability*
to do harm rather than relying on the model to behave:

- **Least privilege.** They advertise only a read-only investigative subset of
  functions to the model. A single allow-list (`KernelProfilePolicy`) drives this, and
  a hard-deny invocation filter enforces the same list as a backstop: any call outside
  the allow-list — every send/mutation, and indeed anything not explicitly permitted —
  is blocked before it executes, regardless of what the model requests.
- **The model never owns the send path.** A standing-rule evaluation cannot send at
  all; it returns a decision plus suggested message text, and *code* then enforces the
  notification cooldown and sends only to the owner's fixed number. A scheduled task's
  output is likewise sent by code to the owner's number. The model cannot choose a
  recipient, so it has no channel to exfiltrate data to a third party even if hijacked.
- **Untrusted input.** Email bodies and calendar event descriptions are externally
  controlled channels that can carry injection payloads. The `StandingRule` profile
  excludes them entirely; the `ScheduledTask` profile allows them (needed for tasks
  like "daily calendar summary") but labels their content as untrusted data in the
  system prompt.

Together these break the "lethal trifecta" (private data + untrusted content + an
exfiltration channel) by removing the channel structurally. The residual risk is that
injection could shape the *content* of a message delivered to the owner's own number —
lower impact, since there is no third-party destination and the owner can sanity-check
it.

**Shared memory and memory laundering.** Ancela's memory is shared across the
instance, so a malicious user could try to smuggle instructions into later runs by
storing them as knowledge or to-dos. We mitigate that in two ways. First, saved memory
keeps provenance metadata about who created it (`userPhoneNumber`), so entries are not
treated as anonymous facts. Second, autonomous profiles explicitly treat retrieved
memory content — like web pages, email bodies, and calendar text — as untrusted data,
not instructions to follow. This provenance tagging does not make stored content
*trusted*; it mainly reduces memory laundering risk by preserving where a memory came
from and making it auditable when the model encounters it again.

## Getting Started

### Local Development

1. **Clone the repository**
   ```bash
   git clone https://github.com/tombly/ancela.git
   cd ancela
   ```
2. **Configure Graph Access**

- Navigate to your organization's Entra admin center [https://aad.portal.azure.com/] and login with a Global administrator account.

- Select a directory and then select App registrations under Manage.

- Select New registration. Enter a name for your application, for example, Ancela AI.

- Set Supported account types to Accounts in this organizational directory only.

- Leave Redirect URI empty.

- Select Register. On the application's Overview page, copy the value of the Application (client) ID and Directory (tenant) ID and save them, you will need these values in the next step.

- Select API permissions under Manage.

- Remove the default User.Read permission under Configured permissions by selecting the ellipses (...) in its row and selecting Remove permission.

- Perform the following steps for each of the permissions `User.Read.All`, `Calendars.ReadWrite`, `Contacts.Read`, `Mail.Read`, and `Mail.Send`:

  - Select Add a permission, then Microsoft Graph.

  - Select Application permissions.

  - Select that permission, then select Add permissions.

- Select Grant admin consent for..., then select Yes to provide admin consent for the selected permission.

- Select Certificates and secrets under Manage, then select New client secret.

- Enter a description, choose a duration, and select Add.

- Copy the secret from the Value column, you will need it in the next step.

3. **Configure user secrets**
   
   Navigate to the AppHost project folder and set your secrets:
   ```bash
   cd Ancela.AppHost
   dotnet user-secrets set Parameters:openai-api-key "your-openai-api-key"
   dotnet user-secrets set Parameters:twilio-account-sid "your-twilio-account-sid"
   dotnet user-secrets set Parameters:twilio-auth-token "your-twilio-auth-token"
   dotnet user-secrets set Parameters:twilio-phone-number "your-twilio-phone-number"
   dotnet user-secrets set Parameters:graph-user-id "your-entra-user-id"
   dotnet user-secrets set Parameters:graph-tenant-id "your-entra-tenant-id"
   dotnet user-secrets set Parameters:graph-client-id "your-graph-app-client-id"
   dotnet user-secrets set Parameters:graph-client-secret "your-graph-app-client-secret"
   dotnet user-secrets set Parameters:ynab-access-token "your-ynab-access-token"
   ```

4. **Set environment variables**

   Two environment variables control Azure resource naming. Set them in your shell profile (e.g. `~/.zshrc`) so they're never committed to the repo:
   ```bash
   export ANCELA_RESOURCE_PREFIX=your-unique-prefix   # prefixed onto all Azure resource names
   export ANCELA_RESOURCE_GROUP=your-resource-group   # used by configure_cosmos.sh
   ```

5. **Run locally**
   ```bash
   dotnet run --project Ancela.AppHost
   ```

   The Aspire dashboard will launch, showing all running services and their endpoints.

### Deployment to Azure

1. **Configure infrastructure**
   
   The project uses .NET Aspire for infrastructure-as-code. Bicep templates generated by Aspire are generated in `Ancela.AppHost/aspire-output/`.

2. **Deploy using Aspire**
   ```bash
   ANCELA_RESOURCE_PREFIX=your-unique-prefix aspire publish
   aspire deploy --clear-cache
   ```

   `aspire publish` bakes the prefix into generated Bicep resource names. `aspire deploy` will prompt you for the resource group name and any required secrets on each run. You can easily grab secret values from user secrets:
   ```bash
   cd Ancela.AppHost
   dotnet user-secrets list | grep Parameters
   ```

3. **Post-deployment configuration**

   After deploying, grant your Entra principal the Contributor role for Cosmos DB so you can access Data Explorer and create the Cosmos DB database and containers:
   ```bash
   ANCELA_RESOURCE_PREFIX=your-unique-prefix ANCELA_RESOURCE_GROUP=your-resource-group ./configure_cosmos.sh
   ```

## Usage

### Starting a Session

Send an SMS to your Twilio number:
```
hello ancela
```

### Interacting

```
Remember that my favorite color is blue
Save a todo: buy milk, eggs, and bread
List my todos
What's on my calendar today?
Do I have any meetings tomorrow?
Create a calendar event for lunch with Sarah next Friday at 1 PM
Do I have any new emails?
Email Sarah and let her know the meeting is rescheduled
What's John's email address?
How much is in my checking account?
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

**Made with ❤️ using .NET Aspire**
