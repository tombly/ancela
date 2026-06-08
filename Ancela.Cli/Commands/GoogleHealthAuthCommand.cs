using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Ancela.Cli.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

/// <summary>
/// One-time Google Health OAuth consent. Runs the authorization-code + PKCE flow in the browser,
/// captures the redirect on a local listener, exchanges the code for tokens, and prints the seed
/// refresh token (plus the user-secrets line to set it). Writes nothing to Cosmos — the agent
/// bootstraps the seed into the oauth_tokens document on first use. Mirrors the print-a-secret UX
/// of <c>enroll</c>.
/// </summary>
public sealed class GoogleHealthAuthCommand : AsyncCommand<GoogleHealthAuthCommand.Settings>
{
    // Read-only Google Health scopes. Must be enabled on the OAuth consent screen.
    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly",
        "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly",
        "https://www.googleapis.com/auth/googlehealth.sleep.readonly",
    ];

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--client-id <ID>")]
        [Description("Google OAuth client id. Defaults to the GOOGLE_HEALTH_CLIENT_ID environment variable.")]
        public string? ClientId { get; init; }

        [CommandOption("--client-secret <SECRET>")]
        [Description("Google OAuth client secret. Defaults to the GOOGLE_HEALTH_CLIENT_SECRET environment variable.")]
        public string? ClientSecret { get; init; }

        [CommandOption("-p|--port <PORT>")]
        [Description("Localhost port for the OAuth redirect; must match the registered callback. Default 4815.")]
        public int Port { get; init; } = 4815;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clientId = settings.ClientId ?? Environment.GetEnvironmentVariable("GOOGLE_HEALTH_CLIENT_ID");
        var clientSecret = settings.ClientSecret ?? Environment.GetEnvironmentVariable("GOOGLE_HEALTH_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            AnsiConsole.MarkupLine(
                "[red]Missing Google OAuth client credentials.[/] Set [white]GOOGLE_HEALTH_CLIENT_ID[/] and [white]GOOGLE_HEALTH_CLIENT_SECRET[/], or pass [white]--client-id[/]/[white]--client-secret[/].");
            return 1;
        }

        var redirectUri = $"http://localhost:{settings.Port}/callback";
        var (verifier, challenge) = Pkce();
        var state = RandomToken(16);
        var authorizeUrl =
            "https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(string.Join(' ', Scopes))}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            // offline + consent guarantee a refresh token is issued (and re-issued on re-consent).
            "&access_type=offline&prompt=consent" +
            $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{settings.Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not listen on port {settings.Port}:[/] {ex.Message}");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[deepskyblue1]Google Health authorization[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Opening your browser to grant read access. If it doesn't open, paste this URL:[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey58]{authorizeUrl}[/]");
        TryOpenBrowser(authorizeUrl);

        var code = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots).SpinnerStyle(Theme.Accent)
            .StartAsync("Waiting for authorization…", _ => WaitForCodeAsync(listener, state, cancellationToken));
        listener.Stop();

        if (code is null)
        {
            AnsiConsole.MarkupLine("[red]No authorization code received.[/] Authorization was denied, mismatched, or cancelled.");
            return 1;
        }

        var refreshToken = await ExchangeCodeAsync(clientId, clientSecret, code, redirectUri, verifier, cancellationToken);
        if (refreshToken is null)
            return 1;

        RenderResult(refreshToken);
        return 0;
    }

    private static async Task<string?> WaitForCodeAsync(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken));
        if (finished != contextTask)
            return null; // cancelled

        var ctx = await contextTask;
        var query = ctx.Request.QueryString;
        var code = query["code"];
        var error = query["error"];
        var state = query["state"];

        var ok = error is null && code is not null && string.Equals(state, expectedState, StringComparison.Ordinal);
        await RespondAsync(ctx.Response, ok, cancellationToken);
        return ok ? code : null;
    }

    private static async Task RespondAsync(HttpListenerResponse response, bool ok, CancellationToken cancellationToken)
    {
        var message = ok
            ? "<h2>Google Health connected ✓</h2><p>You can close this tab and return to the terminal.</p>"
            : "<h2>Authorization failed</h2><p>Return to the terminal and try again.</p>";
        var buffer = Encoding.UTF8.GetBytes($"<html><body style='font-family:sans-serif;text-align:center;padding-top:3em'>{message}</body></html>");
        response.ContentType = "text/html";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, cancellationToken);
        response.Close();
    }

    private static async Task<string?> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, string verifier, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
        };

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            AnsiConsole.MarkupLineInterpolated($"[red]Token exchange failed ({(int)response.StatusCode}):[/] [grey]{body.EscapeMarkup()}[/]");
            return null;
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(token?.RefreshToken))
        {
            AnsiConsole.MarkupLine("[red]No refresh token returned.[/] Ensure the client is a Desktop app and the consent used access_type=offline.");
            return null;
        }
        return token.RefreshToken;
    }

    private static void RenderResult(string refreshToken)
    {
        AnsiConsole.WriteLine();
        var body = new Grid();
        body.AddColumn(new GridColumn().PadRight(2));
        body.AddColumn();
        body.AddRow("[grey]Refresh token[/]", $"[white]{refreshToken}[/]");

        AnsiConsole.Write(new Panel(body)
        {
            Header = new PanelHeader(" google health refresh token "),
            Border = BoxBorder.Rounded,
            BorderStyle = Theme.Accent,
        });
        AnsiConsole.MarkupLine("[grey]Set it as the bootstrap seed (the agent caches and refreshes it thereafter):[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"  [deepskyblue1]cd Ancela.AppHost && dotnet user-secrets set Parameters:google-health-refresh-token \"{refreshToken}\"[/]");
        AnsiConsole.MarkupLine("[grey]To re-consent later, run this again and update that value.[/]");
        AnsiConsole.WriteLine();
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = RandomToken(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
            // Non-fatal: the URL is already printed for manual paste.
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }
}
