using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

namespace Ancela.Agent.Tests;

/// <summary>
/// The owner's mail, calendar and contacts as one mock, so a test can set up all three on the
/// same object (production has one iCloud client for each).
/// </summary>
public interface IOwnerAccounts : IMailClient, ICalendarClient, IContactsClient;
