using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// What the app logs. Console output is kept in Log Analytics, so a log line must not carry what
/// a user wrote or what a tool read; the audit log is the record of those.
/// </summary>
public static class LoggingDefaults
{
    public static ILoggingBuilder AddAncelaLogging(this ILoggingBuilder logging) => logging
        .AddConsole()
        // Information, not Trace: at Trace, Semantic Kernel logs every tool call's arguments and
        // result, and 618 argument and 608 result lines reached Log Analytics in the 30 days to
        // 2026-11-02.
        .SetMinimumLevel(LogLevel.Information)
        // HttpClient logs each request's URL at Information, and some URLs are built from what a
        // tool read: a linked image in an email, a place name to look up.
        .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
}
