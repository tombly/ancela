using System.ComponentModel;
using Ancela.Agent.Services;
using Ancela.Cli.Ui;
using QRCoder;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ancela.Cli.Commands;

/// <summary>
/// Sets up the owner's TOTP secret for step-up on access-management commands and renders it as a
/// QR code to scan into an authenticator app. This is a local, offline command — it touches no
/// Cosmos data; it only derives and displays a secret. With no <c>OWNER_TOTP_SECRET</c> set (or
/// with <c>--new</c>) it mints a fresh secret; otherwise it re-renders the existing one so you can
/// enroll another device.
/// </summary>
public sealed class EnrollCommand : Command<EnrollCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--new")]
        [Description("Mint a brand-new secret instead of rendering the one in OWNER_TOTP_SECRET.")]
        public bool New { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var existing = Environment.GetEnvironmentVariable("OWNER_TOTP_SECRET")?.Trim();
        var reuseExisting = !settings.New && !string.IsNullOrWhiteSpace(existing);
        var secret = reuseExisting ? existing! : TotpService.GenerateSecret();

        var uri = TotpService.BuildOtpAuthUri(secret);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[deepskyblue1]Ancela owner enrollment[/]").LeftJustified());
        AnsiConsole.WriteLine();
        // Write the QR straight to stdout: Spectre would word-wrap lines wider than the console,
        // which shears the code apart. The raw block-character grid must keep its exact widths.
        Console.Out.Write(RenderQr(uri));
        AnsiConsole.WriteLine();

        var currentCode = TotpService.ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var body = new Grid();
        body.AddColumn(new GridColumn().PadRight(2));
        body.AddColumn();
        body.AddRow("[grey]Secret[/]", $"[white]{secret}[/]");
        body.AddRow("[grey]otpauth[/]", $"[grey58]{uri.EscapeMarkup()}[/]");
        body.AddRow("[grey]Code now[/]", $"[springgreen2]{currentCode}[/] [grey](should match your app)[/]");

        AnsiConsole.Write(new Panel(body)
        {
            Header = new PanelHeader(reuseExisting ? " existing secret " : " new secret "),
            Border = BoxBorder.Rounded,
            BorderStyle = Theme.Accent,
        });

        AnsiConsole.MarkupLine(
            "[grey]Scan the QR, or type the secret into your authenticator manually. Then set it so the agent enforces step-up:[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"  [deepskyblue1]cd Ancela.AppHost && dotnet user-secrets set Parameters:owner-totp-secret \"{secret}\"[/]");
        AnsiConsole.MarkupLine(
            "[grey]Once configured, [white]invite[/]/[white]revoke[/] require a current code, e.g. [white]invite +15551234567 408291[/]. Leave it unset to disable step-up.[/]");
        AnsiConsole.WriteLine();
        return 0;
    }

    // Renders the otpauth URI as a scannable terminal QR. Low ECC keeps the module count (and so
    // the width) down; the otpauth payload is short and re-displayable, so heavy error correction
    // buys nothing here.
    private static string RenderQr(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        return new AsciiQRCode(data).GetGraphic(1, "██", "  ");
    }
}
