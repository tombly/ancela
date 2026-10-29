# Ancela

Ancela is an experimental, single-owner AI assistant you talk to by text message.
Send it an SMS and it can manage your calendar and email, track to-dos and projects,
look things up on the web, watch for conditions and remind you — acting on your behalf
across the real-world services it's connected to.

> **Experimental personal project, not a product.** It's wired to one owner's accounts
> and makes deliberate trust trade-offs (see [Security & trust model](#security--trust-model))
> that only make sense for a personal, self-hosted deployment.

## What it can do

You interact entirely over SMS. Under the hood the assistant has a set of capabilities:

- **Calendar & email** — read your calendar, create events, read your inbox (text attachments too, such as a CSV export or a DMARC report), mark emails read and move them to the trash, and send mail (via Microsoft Graph). The owner can also save an email as a PDF to one OneDrive folder.
- **Contacts** — look up people's details from your address book.
- **Money** — check account balances and recent transactions (via YNAB).
- **Memory** — remember facts about you and keep a shared list of to-dos. It also keeps instructions for how to behave with you from now on (a persona, what to call you, a preference).
- **Projects** — longer-lived workspaces for bigger efforts, with freeform notes and trackable, categorized entries (a trip, a list to work through over time, ideas to collect).
- **Reminders** — schedule a one-time SMS reminder for a specific time.
- **Standing rules** — watch a recurring condition on a timer and notify you when it's met.
- **Scheduled tasks** — run a recurring action on a clock schedule and report back (e.g. a daily calendar summary).
- **Web** — search the web and fetch page content to answer questions, and look up who holds an IP address in the internet registries.
- **reMarkable** — send a document to your reMarkable tablet (owner only).

### Example prompts

```
hello ancela                                    ← start a session
Remember that my favorite color is blue
Add a to-do: buy milk, eggs, and bread
What's on my calendar tomorrow?
Create a lunch with Sarah next Friday at 1 PM
Any new emails? Reply to Sarah that I'm running late
What's John's email address?
How much is in my checking account?
Remind me to call the dentist at 9am Monday
Every morning at 7, text me my calendar for the day
Let me know if I get an email from the landlord
Every morning at 8, summarize yesterday's DMARC reports, then trash them
Start a project for our backpacking trip and add a packing list
From now on, answer like HAL 9000
```

## Architecture

Ancela is a cloud-native .NET application orchestrated with [Aspire](https://learn.microsoft.com/dotnet/aspire/).

The message flow:

1. A user texts the assistant's **Twilio** number.
2. Twilio posts the message to an **Azure Functions** HTTP trigger, which enqueues it to **Azure Service Bus** and returns immediately.
3. A queue processor picks up the message — one at a time, so a text sent right after another sees it in history. A lightweight interceptor handles session and access commands (`hello ancela`, `invite`, `revoke`); everything else is handed to the agent.
4. The **agent** builds a [Semantic Kernel](https://learn.microsoft.com/semantic-kernel/) instance per request, exposes its capabilities as kernel functions, and lets **OpenAI (`gpt-6-luna`, with reasoning off)** decide which to call via automatic function calling.
5. Those functions read and write **Azure Cosmos DB** and reach out to Microsoft Graph, YNAB, the web (Tavily), the internet registries, Twilio, and reMarkable as needed. The reply goes back to the user as an SMS.

**Tools.** Each capability is a small, general tool (search email, read one, read an attachment, look up an IP address, mark an email read, move one to the trash), and the model combines them to carry out a request. A new kind of request should rarely need a new tool: the DMARC summary above is a scheduled task over those six, with no DMARC code anywhere. A tool is built for one purpose only when the model has been shown to fail at the combination, when a general tool would be too powerful for the trust model below, or when content should move without passing through the model. [AGENTS.md](AGENTS.md#tool-design-small-tools-the-model-combines) has the reasoning and examples.

**Autonomous work** (standing rules and scheduled tasks) runs the same agent from a timer/queue with no user present, under a restricted kernel profile (see [Security & trust model](#security--trust-model)).

![Design](Images/design.svg)

*The diagram shows the core chat path. Autonomous work (reminders, standing rules, scheduled tasks) runs the same agent off its own Service Bus queues and is described above.*

**Tech stack:** .NET 10 / C# 13 · Aspire (orchestration + Bicep infrastructure-as-code) · Azure Functions (isolated worker) · Azure Cosmos DB · Azure Service Bus · Semantic Kernel + OpenAI · Twilio · Microsoft Graph · YNAB · reMarkable.

A companion CLI (`Ancela.Cli`) browses the Cosmos containers for auditing, runs owner TOTP enrollment, and lets the owner chat with Ancela from the terminal when SMS can't reach them (`ancela chat`, below).

## Security & trust model

Ancela is a **single-owner** assistant. Each deployed instance is wired to exactly
one owner's accounts — one Microsoft Graph identity (`GRAPH_USER_ID`: mail, calendar,
contacts) and one YNAB token — using app-only credentials held by the instance. Those
credentials reach only the owner's mailbox and one OneDrive folder, not the whole tenant
(see [step 1](#1-configure-microsoft-graph-access) of the setup).

The owner authorizes a **small, trusted set of people** (by phone number) to talk to
the agent over SMS. Those users act *through* the agent and can therefore reach the
owner's connected data — read the calendar, contacts and finances. **This is
intentional.** Email is the exception: only the owner can read it, so other users see no
email content at all, and sending (SMS and email) and creating calendar events are
owner-only too. Authorized users are trusted
principals invited by the owner; they are not mutually-isolated tenants, and the
agent is not a multi-tenant SaaS. Reviewers should treat cross-user access to the
owner's data as by-design, not as a vulnerability.

Data model implications:
- All Cosmos containers partition on `/agentPhoneNumber` (one value per instance).
- **Knowledge, to-dos, and projects are shared** across the instance's authorized
  users — it is one shared memory, by design.
- **Chat history is per-user** (filtered by `userPhoneNumber`).
- Reminders, standing rules, and scheduled tasks are created and listed per-user.

Access boundary: only the owner self-registers; everyone else must be `invite`d by
the owner (by phone number) before `hello ancela` does anything, and the owner can
`revoke` them. Identity, though, still rests on the SMS sender number, which is
spoofable — so the highest-value privilege (the owner's own `invite`/`revoke`) is the
weakest point if someone spoofs the owner's number.

`ancela chat` is the one way in besides SMS. It puts messages on the same `chat-messages`
queue, and the queue takes each message's sender number at face value, so being able to send
to it is being able to talk as any user. Only two things send to it: the Twilio webhook, which
checks Twilio's request signature, and the owner's own Entra account, which signs in with
`az login` and holds the Azure Service Bus Data Sender role on that one queue. The namespace
accepts no connection-string keys.

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
(`web_search` / `web_fetch`, and the registry records `lookup_ip` reads, which an address's
holder writes) is not trusted, and neither is any external page reached
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
  is blocked before it executes, regardless of what the model requests. There is one
  exception, below.
- **The model never owns the send path.** A standing-rule evaluation cannot send at
  all; it returns a decision plus suggested message text, and *code* then enforces the
  notification cooldown and sends only to the owner's fixed number. A scheduled task's
  output is likewise sent by code to the owner's number. The model cannot choose a
  recipient, so it has no channel to exfiltrate data to a third party even if hijacked.
- **Email it read, and only after the work.** A scheduled task can mark an email read
  (`mark_email_read`) and move it to Deleted Items (`trash_email`), so a task that processes
  emails, such as DMARC reports, can clear them away. The guard lets it change only an email
  the same run has read, with no read of it failing: a report whose attachment couldn't be read
  stays unread in the inbox, and text planted in one email can't reach an email the run never
  opened. The owner can restore anything from Deleted Items or mark it unread again, and each
  change is in the audit log. These are the only changes an autonomous run can make.
- **Untrusted input.** Email bodies, attachments and calendar event descriptions are externally
  controlled channels that can carry injection payloads. The `StandingRule` profile
  excludes them entirely; the `ScheduledTask` profile allows them (needed for tasks
  like "daily calendar summary") but labels their content as untrusted data in the
  system prompt.

Together these break the "lethal trifecta" (private data + untrusted content + an
exfiltration channel) by removing the channel structurally. The residual risk is that
injection could shape the *content* of a message delivered to the owner's own number —
lower impact, since there is no third-party destination and the owner can sanity-check
it.

**Chat after untrusted content.** In chat a person is in the loop, but the model can still
act on text an outsider wrote before that person sees anything. So once a message has read
email (attachments included) or calendar content, sending (SMS and email), `web_fetch` (a URL can carry data out)
and saving or removing instructions are switched off for the rest of that message. After a
web search, a fetch or an IP lookup, sending and instructions are switched off. An IP lookup
asks the internet registries, not the address's own DNS server (a reverse lookup), so the
addresses Ancela looks up after reading email never reach a server an outsider runs. The same guard filter
enforces this in code. Ancela says what it was about to do, and the user confirms in their
next message, which starts with nothing read, so replying to an email takes two texts.
Saving an email to OneDrive, marking one read, moving one to Deleted Items and creating a calendar event stay
on, since they touch only the owner's own folder, mailbox and calendar. Images an email links to are downloaded, for a PDF
of it, only when the owner says yes, since downloading them tells the sender the email was
opened; the download goes only to public addresses, so a planted link can't reach anything
inside Azure. This doesn't cover everything: text from a read
can carry over in Ancela's reply, which is kept in history, and knowledge or to-dos saved
after a read aren't marked when they're read back later.

**Shared memory and memory laundering.** Ancela's memory is shared across the
instance, so a malicious user could try to smuggle instructions into later runs by
storing them as knowledge or to-dos. We mitigate that in two ways. First, saved memory
keeps provenance metadata about who created it (`userPhoneNumber`), so entries are not
treated as anonymous facts. Second, autonomous profiles explicitly treat retrieved
memory content — like web pages, email bodies, and calendar text — as untrusted data,
not instructions to follow. This provenance tagging does not make stored content
*trusted*; it mainly reduces memory laundering risk by preserving where a memory came
from and making it auditable when the model encounters it again.

## Build & run

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Aspire tooling](https://learn.microsoft.com/dotnet/aspire/fundamentals/setup-tooling) and the Azure CLI for deployment
- Accounts/keys: OpenAI, Twilio (with a phone number), a Microsoft Entra app registration, and (optionally) YNAB

### 1. Configure Microsoft Graph access

In your organization's [Entra admin center](https://aad.portal.azure.com/), create an
**App registration** (single-tenant, no redirect URI) and note its **Application
(client) ID** and **Directory (tenant) ID**. Then create a **client secret** under
**Certificates & secrets** and copy its value.

The app reaches only the owner's mailbox and one OneDrive folder. An *Application*
permission granted the usual way, under the registration's **API permissions**, covers the
whole tenant: `Mail.Send` there would let the app send as anyone in it. So mail, calendar and
contacts are granted in Exchange Online instead, limited to the owner's mailbox
([RBAC for Applications](https://learn.microsoft.com/exchange/permissions-exo/application-rbac)).
In Exchange Online PowerShell (`Connect-ExchangeOnline`, as an Exchange administrator):

```powershell
New-ServicePrincipal -AppId <client ID> -ObjectId <enterprise application object ID> -DisplayName Ancela
$owner = (Get-Mailbox <owner's email address>).ExternalDirectoryObjectId   # the owner's Entra object ID
New-ManagementScope -Name "Ancela owner" -RecipientRestrictionFilter "ExternalDirectoryObjectId -eq '$owner'"
"Mail.ReadWrite", "Mail.Send", "Calendars.ReadWrite", "Contacts.Read" | ForEach-Object {
    New-ManagementRoleAssignment -App <client ID> -Role "Application $_" -CustomResourceScope "Ancela owner"
}
Test-ServicePrincipalAuthorization -Identity <client ID> -Resource <owner's email address>
```

The test should list all four roles with `InScope` `True`; `False` means the scope's filter
matches no mailbox (`(Get-ManagementScope "Ancela owner").RecipientFilter` shows it, and
`Set-ManagementScope` fixes it without redoing the assignments). `$owner` is a GUID, the same
value as the `graph-user-id` secret below; the filter matches nothing if given an email address.
The enterprise application's object ID is on its own Overview page: open it from the app
registration's **Managed application in local directory** link, since the Enterprise
applications list hides some apps unless filtered to **All applications**. App registrations
shows a different object ID.
A change takes 30 minutes to 2 hours to reach Graph. `Mail.ReadWrite` covers reading email,
marking it read (`mark_email_read`) and moving it to Deleted Items (`trash_email`). The app needs no directory permission such as
`User.Read.All`: it only ever addresses the owner's mailbox by that ID.

**Moving an existing setup over.** Earlier versions of this guide granted `User.Read.All`,
`Mail.Read`, `Mail.Send`, `Calendars.ReadWrite` and `Contacts.Read` under **API
permissions**. The two kinds of grant add up, so those keep their reach to every mailbox until
they're removed. Make the Exchange assignments above first, and once the test lists them, give
them two hours. Then remove the five from **API permissions** and revoke their admin consent.
Removed any sooner, Ancela loses email, calendar and contacts until the Exchange grants take
effect. Afterwards, `check_services` and the live mailbox tests (`MailSearchLiveTests`) show
that everything still works; `TrashEmail_MovesAnEmailToDeletedItems` passes only through
Exchange, since Entra never granted `Mail.ReadWrite`.

To save emails as PDFs (`save_email_to_onedrive`), add the *Application* permission
`Files.SelectedOperations.Selected` under **API permissions** and grant admin consent: the only
permission granted in Entra. On its own it grants nothing;
you then give the app write access to **one folder**. Don't use `Files.ReadWrite.All`
instead: it would let the app write to every OneDrive in the tenant.

1. Create the folder in the owner's OneDrive for work or school. App-only credentials can't
   reach a personal Microsoft account's OneDrive.
2. In [Graph Explorer](https://aka.ms/ge), signed in as the owner (check with `GET /me`; a
   different account gets `itemNotFound`), run `GET /me/drive/root:/<folder>` and note `id`
   and `parentReference.driveId`.
3. Grant the app that folder:
   ```http
   POST https://graph.microsoft.com/v1.0/drives/{driveId}/items/{folderId}/permissions

   { "grantedToV2": { "application": { "id": "<client ID>" } }, "roles": ["write"] }
   ```
   Expect `201`. If Graph Explorer gets `403`, consent `Sites.FullControl.All` for your own
   Graph Explorer session (not the app) and retry.

To revoke, delete that permission from the folder. The self-check (`check_services`) reports
the grant as `onedrive-folder`.

### 2. Set user secrets

From the AppHost project:

```bash
cd Ancela.AppHost
dotnet user-secrets set Parameters:openai-api-key      "..."
dotnet user-secrets set Parameters:twilio-account-sid  "..."
dotnet user-secrets set Parameters:twilio-auth-token   "..."
dotnet user-secrets set Parameters:twilio-phone-number "..."
dotnet user-secrets set Parameters:graph-user-id       "..."   # the owner's Entra user ID
dotnet user-secrets set Parameters:graph-tenant-id     "..."
dotnet user-secrets set Parameters:graph-client-id     "..."
dotnet user-secrets set Parameters:graph-client-secret "..."
dotnet user-secrets set Parameters:graph-onedrive-drive-id  "..."   # the granted folder's driveId
dotnet user-secrets set Parameters:graph-onedrive-folder-id "..."   # the granted folder's id
dotnet user-secrets set Parameters:ynab-access-token   "..."   # optional
```

To use owner step-up (`invite`/`revoke`), also set `OWNER_TOTP_SECRET` to the value
produced by `ancela enroll` (see [Security & trust model](#security--trust-model)).

### 3. Set resource-naming environment variables

Two variables control Azure resource naming. Put them in your shell profile (e.g.
`~/.zshrc`) so they're never committed:

```bash
export ANCELA_RESOURCE_PREFIX=your-unique-prefix   # prefixed onto all Azure resource names
export ANCELA_RESOURCE_GROUP=your-resource-group   # used by configure_cosmos.sh
```

### 4. Run locally

```bash
dotnet run --project Ancela.AppHost
```

The Aspire dashboard launches and shows every service and its endpoints.

### Deploy to Azure

The container image is built on your machine, so Docker must be running. Resource names
come from `ANCELA_RESOURCE_PREFIX` (step 3) at deploy time; if it isn't set, the deploy
stops with an error.

**The first deploy** asks for the Azure subscription, resource group and location, and
for each secret:

```bash
aspire deploy
```

You can pull secret values from user secrets to paste in:

```bash
cd Ancela.AppHost && dotnet user-secrets list | grep Parameters
```

Then grant your Entra principal the Contributor role on Cosmos DB, and create the database
and containers:

```bash
ANCELA_RESOURCE_PREFIX=your-unique-prefix ANCELA_RESOURCE_GROUP=your-resource-group ./configure_cosmos.sh
```

**Every deploy after that** reuses those answers, which Aspire caches under
`~/.aspire/deployments/`, so nothing is asked:

```bash
aspire deploy --non-interactive --project Ancela.AppHost/Ancela.AppHost.csproj
```

Don't add `--clear-cache` to a routine deploy. It throws away the cached resource group,
location and subscription along with the secrets, so the next deploy asks for all of them
again, and answering differently from the first time points it at something other than
your live deployment. If you add a new secret parameter, set it in user secrets first; if a
deploy still needs a value it doesn't have, run it without `--non-interactive` so it can ask.

`aspire publish` writes the generated Bicep to `Ancela.AppHost/aspire-output/` for review;
deploying doesn't need it.

### Chat from the terminal

`ancela chat` talks to Ancela without SMS: on a plane with Wi-Fi, or anywhere a text won't get
through. It looks and works like a terminal chat (Claude Code's, roughly): each tool Ancela
calls appears as it finishes, then the reply. Replies come back to the terminal, not by text.
Between messages it shows anything Ancela texted you meanwhile (reminders, scheduled tasks,
rule alerts).

It signs in with your `az login`, finds Cosmos and Service Bus from `ANCELA_RESOURCE_PREFIX`,
and needs to know which number is yours:

```bash
export ANCELA_OWNER_PHONE_NUMBER=+15551234567   # the number you text Ancela from
```

Sending needs one role, once, on the chat queue only:

```bash
az role assignment create \
  --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Azure Service Bus Data Sender" \
  --scope "$(az servicebus namespace show -g "$ANCELA_RESOURCE_GROUP" -n "$ANCELA_RESOURCE_PREFIX-servicebus" --query id -o tsv)/queues/chat-messages"
```

Then:

```bash
dotnet run --project Ancela.Cli -- chat                         # a conversation
dotnet run --project Ancela.Cli -- chat "what's on my list?"    # one message
```

In a conversation, Enter sends, Shift+Enter or Option+Enter adds a line, Up and Down recall
what you typed, Esc stops waiting for a reply (it still shows when it arrives), and `/help`
lists the commands (`/history`, `/clear`, `/exit`).

## License

MIT — see [LICENSE](LICENSE).

---

**Made with ❤️ using Aspire**
