using Ancela.Agent.SemanticKernel.Plugins.DiagnosticsPlugin;
using FluentAssertions;

namespace Ancela.Agent.Tests;

/// <summary>
/// check_anomalies' guard-denial count is a Cosmos query over the guard's error text. The markers
/// once went into the query as quoted text, and the grant marker's apostrophe ("hasn't") made every
/// run a syntax error (2026-11-04 to 2026-11-15). They go in as parameters.
/// </summary>
public class AuditAnomalyQueryTests
{
    [Fact]
    public void TheDenialMarkers_AreParameters_NotTextInTheQuery()
    {
        var query = AuditAnomalyScanner.GuardDenialsQuery("+15550000000", "2026-11-15T00:00:00Z");

        foreach (var marker in AuditAnomalyScanner.DenialMarkers)
            query.QueryText.Should().NotContain(marker);
        query.QueryText.Should().NotContain("'", "any quoted text in the query is where a marker's apostrophe broke it");
        query.GetQueryParameters().Select(p => p.Value).Should().Contain(AuditAnomalyScanner.DenialMarkers);
    }

    [Fact]
    public void EveryGuardDenial_HasItsMarker()
    {
        AuditAnomalyScanner.DenialMarkers.Should().Contain(
        [
            AutonomousToolGuardFilter.NotGrantedDenialMarker,
            AutonomousToolGuardFilter.UntrustedContentDenialMarker,
            AutonomousToolGuardFilter.UnreadEmailDenialMarker,
        ]);
        AuditAnomalyScanner.DenialMarkers.Should().Contain(m => m.Contains('\''),
            "a marker with an apostrophe is the case that broke the query");
    }
}
