namespace MissionControl.Core;

/// <summary>Delete-only upgrade boundary. This application no longer reads or stores provider credentials.</summary>
public interface ILegacyCredentialStore
{
    void Delete(string key);
    void DeleteScope(string prefix);
}
