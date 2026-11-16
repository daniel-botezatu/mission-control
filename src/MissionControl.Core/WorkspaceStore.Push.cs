namespace MissionControl.Core;

public sealed record PushObservation(long Sequence, EvidenceRecord? Record);

public sealed partial class WorkspaceStore
{
    private bool pushJournalEnabled;
    public string PushJournalId => GetSetting("alexa.push.id") ?? throw new InvalidOperationException("PUSH_JOURNAL_DISABLED");

    /// <summary>Optional bridge journal stores only record pointers, never another transcript copy.</summary>
    public void EnablePushJournal()
    {
        lock (gate)
        {
            var initialized = GetSetting("alexa.push.initialized") == "1";
            var journalId = GetSetting("alexa.push.id") ?? Guid.NewGuid().ToString();
            using var transaction = connection.BeginTransaction();
            if (!initialized)
            {
                ProjectExecute(transaction, "INSERT INTO alexa_push_events(record_id) SELECT id FROM evidence ORDER BY ingested_at,id");
                ProjectExecute(transaction, "INSERT INTO settings(key,value) VALUES('alexa.push.initialized','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            }
            ProjectExecute(transaction, "INSERT INTO settings(key,value) VALUES('alexa.push.id',$id) ON CONFLICT(key) DO NOTHING", ("$id", journalId));
            transaction.Commit();
            pushJournalEnabled = true;
        }
    }

    public IReadOnlyList<PushObservation> ReadPushObservations(long after, int limit = 100)
    {
        lock (gate)
        {
            if (!pushJournalEnabled || after < 0) throw new ArgumentException("INVALID_PUSH_CURSOR");
            var pointers = new List<(long Sequence, string Id)>();
            using (var command = Command("SELECT sequence,record_id FROM alexa_push_events WHERE sequence>$after ORDER BY sequence LIMIT $limit",
                ("$after", after), ("$limit", Math.Clamp(limit, 1, 100))))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) pointers.Add((reader.GetInt64(0), reader.GetString(1)));
            // A revoked project's pointer is consumed without disclosing its record.
            return pointers.Select(pointer => new PushObservation(pointer.Sequence, GetRecord(pointer.Id))).ToArray();
        }
    }
}
