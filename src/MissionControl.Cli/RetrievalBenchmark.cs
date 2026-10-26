using System.Diagnostics;
using System.Text.Json;
using MissionControl.Core;

internal static class RetrievalBenchmark
{
    private sealed record Entry(string Id, string Text);
    private sealed record Question(string QuestionText, string Query, string[] Relevant);
    private sealed record Dataset(Entry[] Records, Question[] Questions);
    public static async Task RunAsync(string directory, string datasetFile)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new ArgumentException("Benchmark requires a fresh disposable data directory.");
        var dataset = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(datasetFile), AppController.Json)!;
        Directory.CreateDirectory(directory); var root = Path.Combine(directory, "project"); Directory.CreateDirectory(root);
        var process = Process.GetCurrentProcess(); var initialCpu = process.TotalProcessorTime;
        var database = Path.Combine(directory, "memory.db");
        var index = Stopwatch.StartNew();
        using (var writer = new WorkspaceStore(database))
        {
            writer.SaveProject(new("benchmark", "Synthetic benchmark", "#5470cc", "Synthetic", root)); writer.SetProjectApproval("benchmark", true);
            writer.SaveSource(new("fixture", "codex", "Synthetic benchmark", directory));
            writer.CommitBatch(dataset.Records.Select((record, i) => new EvidenceRecord(record.Id,"fixture","synthetic.jsonl","session-"+i,"benchmark","codex","message","Developer message",record.Text,"user","2026-10-20T10:00:00Z","2026-10-20T10:00:00Z",WorkingDirectory:root,DataJson:JsonSerializer.Serialize(new { schema="memory-v1",phase="user",exchangeId="exchange-"+i,sourceRecordId=record.Id,sourceByteOffset=i*128 }))).ToArray());
        }
        index.Stop();
        List<object> rows = []; List<double> timings = []; double recalls=0, reciprocal=0;
        foreach (var question in dataset.Questions)
        {
            var timer=Stopwatch.StartNew();
            using var reader=WorkspaceStore.OpenReadOnly(database);
            var result=await new HistoryTools(reader).CallAsync("search_history",JsonSerializer.SerializeToElement(new { query=question.Query,limit=5 }));
            timer.Stop(); timings.Add(timer.Elapsed.TotalMilliseconds);
            if(result.IsError) throw new InvalidOperationException(result.Json);
            using var json=JsonDocument.Parse(result.Json); var ids=json.RootElement.GetProperty("records").EnumerateArray().Select(r=>r.GetProperty("id").GetString()!).ToArray();
            var recall=(double)ids.Intersect(question.Relevant).Count()/question.Relevant.Length;
            var rank=Array.FindIndex(ids,id=>question.Relevant.Contains(id))+1; recalls+=recall; reciprocal+=rank>0?1.0/rank:0;
            rows.Add(new { question.QuestionText,question.Query,expected=question.Relevant,returned=ids,recallAt5=recall,reciprocalRank=rank>0?1.0/rank:0,milliseconds=timer.Elapsed.TotalMilliseconds });
        }
        process.Refresh(); var ordered=timings.Order().ToArray();
        var report=new { scenario="synthetic-lexical-retrieval", dataset=Path.GetFullPath(datasetFile), records=dataset.Records.Length, questions=dataset.Questions.Length, recallAt5=recalls/dataset.Questions.Length,meanReciprocalRank=reciprocal/dataset.Questions.Length,p50Milliseconds=ordered[ordered.Length/2],p95Milliseconds=ordered[(int)Math.Ceiling(ordered.Length*.95)-1],indexMilliseconds=index.Elapsed.TotalMilliseconds,process.WorkingSet64,process.PrivateMemorySize64,cpuSeconds=(process.TotalProcessorTime-initialCpu).TotalSeconds,databaseBytes=new FileInfo(database).Length,rows,limitations="Synthetic lexical relevance labels, not host reasoning or end-user answer quality. Each query opens a fresh read-only connection; OS disk cache is not flushed." };
        var output=JsonSerializer.Serialize(report,AppController.Json); File.WriteAllText(Path.Combine(directory,"retrieval.json"),output); Console.WriteLine(output);
    }
}
