namespace Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin.Models;

/// <summary>
/// Raw activity counts gathered from the audit container for one scan window. Deliberately
/// judgment-free: <see cref="AnomalyEvaluator"/> turns these numbers into findings, so the
/// threshold logic stays pure and unit-testable without Cosmos.
/// </summary>
public class AuditActivitySummary
{
    public required int WindowHours { get; init; }

    /// <summary>Inbound messages dropped because the sender has no account / wasn't invited.</summary>
    public int UnknownSenderMessageCount { get; init; }

    /// <summary>Distinct unknown sender numbers (capped — see the scanner).</summary>
    public string[] UnknownSenders { get; init; } = [];

    /// <summary>Owner invite/revoke attempts that failed TOTP step-up.</summary>
    public int FailedStepUpCount { get; init; }

    /// <summary>Invocations hard-denied by <c>AutonomousToolGuardFilter</c>.</summary>
    public int GuardDenialCount { get; init; }

    public int InviteCount { get; init; }
    public int RevokeCount { get; init; }
    public int DeregisterCount { get; init; }

    public int WebSearchCount { get; init; }
    public int SmsSendCount { get; init; }
    public int EmailSendCount { get; init; }

    /// <summary>Failed tool invocations grouped by plugin and function.</summary>
    public FunctionFailureGroup[] FailedFunctions { get; init; } = [];

    /// <summary>The most recent failed invocations, with their error text.</summary>
    public RecentFailure[] RecentFailures { get; init; } = [];
}

public class FunctionFailureGroup
{
    public required string Plugin { get; init; }
    public required string Function { get; init; }
    public required int Count { get; init; }
}

public class RecentFailure
{
    public required string Plugin { get; init; }
    public required string Function { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
