using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MissionControl.Core;

public sealed record AlexaProjectScope(string Name, string RootPath, bool Approved);

public sealed partial class WorkspaceStore
{
    /// <summary>Replace approvals in a dedicated Alexa bridge store, never the desktop store.
    /// Removed roots remain denied so an approved parent cannot absorb their history.</summary>
    public bool SynchronizeAlexaScopes(IReadOnlyList<AlexaProjectScope> scopes)
    {
        if (scopes.Count > 2000) throw new ArgumentException("TOO_MANY_SCOPES");
        var values = scopes.Select(scope =>
        {
            var root = NormalizePrivacyPath(scope.RootPath);
            if (root is null || BroadDiscoveryRoot(Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope.RootPath))) || string.IsNullOrWhiteSpace(scope.Name))
                throw new ArgumentException("INVALID_PROJECT_SCOPE");
            var identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
            return (Id: "alexa-scope-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24],
                Root: Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope.RootPath)), Name: Redactor.Clean(scope.Name, 200), scope.Approved);
        }).OrderBy(scope => scope.Id).ToArray();
        if (values.Select(v => v.Id).Distinct().Count() != values.Length) throw new ArgumentException("DUPLICATE_PROJECT_SCOPE");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            values.Select(v => new { v.Id, v.Root, v.Name, v.Approved })))));
        lock (gate)
        {
            if (GetSetting("alexa.scopes.fingerprint") == fingerprint) return false;
            using var transaction = connection.BeginTransaction();
            try
            {
                // This opt-in API owns the complete allowlist in its separate bridge DB.
                // Existing root IDs are reused to avoid ambiguous equal-root mappings.
                ProjectExecute(transaction, "UPDATE projects SET is_approved=0,approved_at=NULL");
                foreach (var value in values)
                {
                    var existing = ReadProjectRoots(transaction).Where(p => p.Priority == 0 &&
                        string.Equals(p.Root, NormalizeDiscoveryPath(value.Root), ProjectPathComparison)).Select(p => p.Project).FirstOrDefault();
                    var id = existing ?? value.Id;
                    ProjectExecute(transaction, """
                        INSERT INTO projects(id,name,color,description,root_path,origin,is_approved,approved_at)
                        VALUES($id,$name,'#5470cc','Alexa synchronized scope',$root,'manual',$approved,$at)
                        ON CONFLICT(id) DO UPDATE SET name=excluded.name,root_path=excluded.root_path,
                        is_approved=excluded.is_approved,approved_at=excluded.approved_at
                        """, ("$id", id), ("$name", value.Name), ("$root", value.Root), ("$approved", value.Approved),
                        ("$at", value.Approved ? DateTimeOffset.UtcNow.ToString("O") : null));
                    projectRoots = null;
                }
                ReassignProjectEvidenceUnsafe(transaction);
                ProjectExecute(transaction, "DELETE FROM evidence WHERE id IN (SELECT r.id FROM evidence r WHERE NOT (" + ApprovalPredicate + "))");
                ProjectExecute(transaction, "DELETE FROM session_corrections WHERE NOT EXISTS(SELECT 1 FROM evidence e WHERE e.source_id=session_corrections.source_id AND e.session_id=session_corrections.session_id); DELETE FROM checkpoints");
                RebuildApprovedSearchIndexes(transaction);
                ProjectExecute(transaction, "INSERT INTO settings(key,value) VALUES('alexa.scopes.fingerprint',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$value", fingerprint));
                Invalidate(transaction);
                transaction.Commit();
                Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum");
                return true;
            }
            finally { projectRoots = null; }
        }
    }
}
