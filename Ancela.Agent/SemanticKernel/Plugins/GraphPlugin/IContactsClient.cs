namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// The owner's address book, as the contact tools use it: Microsoft 365 through Graph (<see cref="GraphClient"/>)
/// or iCloud over CardDAV (<see cref="CardDavContactsClient"/>), as CONTACTS_PROVIDER says (DependencyModule).
/// </summary>
public interface IContactsClient
{
    Task<ContactModel[]> GetUserContactsAsync(int maxResults = 100);

    /// <summary>The contact whose name, first name or last name starts with <paramref name="name"/>, or null for none.</summary>
    Task<ContactModel?> GetUserContactByNameAsync(string name);
}
