using Microsoft.Data.Sqlite;

namespace VPSnode.Core;

public sealed record InspectionTask(string Id, string Kind, string TargetId, string Source,
    string State, DateTimeOffset Created, DateTimeOffset Updated, Inspection? Result = null,
    string ErrorCode = "", string Error = "", bool CancelRequested = false, int ExecutorPid = 0)
{
    public bool Terminal => State is "completed" or "failed" or "canceled";
    public int Total => Result?.Items.Length ?? 0;
    public int Succeeded => Result?.Success ?? 0;
    public int Failed => Result?.Failed ?? 0;
    public int Skipped => Result?.Skipped ?? 0;
    public int Remaining => Result?.Remaining ?? 0;
}

public sealed partial class Store
{
    public InspectionTask[] Tasks => List<InspectionTask>("inspection-task").OrderByDescending(t => t.Created).ToArray();

    public InspectionTask? GetTask(string id)
    {
        using var db = Open();
        return ReadTask(db, null, id);
    }

    private static InspectionTask? ReadTask(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var q = Command(db, tx, "SELECT data FROM assets WHERE kind='inspection-task' AND id=$i", ("$i", id));
        return q.ExecuteScalar() is byte[] data ? Unprotect<InspectionTask>(data) : null;
    }

    public InspectionTask SubmitTask(string kind, string targetId, string source = "CLI")
    {
        ValidateTask(kind, targetId);
        using var db = Open(); using var tx = db.BeginTransaction();
        var task = SubmitTask(db, tx, kind, targetId, source);
        tx.Commit(); return task;
    }

    private static InspectionTask SubmitTask(SqliteConnection db, SqliteTransaction tx, string kind, string id, string source)
    {
        var active = List<InspectionTask>(db, tx, "inspection-task")
            .FirstOrDefault(t => t.Kind == kind && t.TargetId == id && !t.Terminal);
        if (active != null) return active;
        var now = DateTimeOffset.UtcNow;
        var task = new InspectionTask(Guid.NewGuid().ToString("N"), kind, id, source, "queued", now, now);
        Put(db, tx, "inspection-task", task.Id, task);
        return task;
    }

    public void ValidateTask(string kind, string targetId)
    {
        bool exists = kind switch
        {
            "all" or "scheduled" or "nodes" or "speeds" or "websites" => targetId.Length == 0,
            "host" or "inventory" or "host-nodes" => Hosts.Any(h => h.Id == targetId),
            "web" or "tls" => WebProbe.Targets(this).Any(w => w.Id == targetId),
            "node" or "speed" or "sni" => Nodes.Any(n => n.Id == targetId),
            _ => throw new InvalidDataException("检测类型无效。")
        };
        if (!exists) throw new KeyNotFoundException("未找到检测对象，或批量检测不接受目标 ID。");
    }

    public InspectionTask CancelTask(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var task = ReadTask(db, tx, id) ?? throw new KeyNotFoundException("未找到任务。");
        if (!task.Terminal)
        {
            task = task with { CancelRequested = true, Updated = DateTimeOffset.UtcNow,
                State = task.State == "queued" ? "canceled" : task.State,
                ErrorCode = "canceled", Error = "用户请求取消" };
            Put(db, tx, "inspection-task", id, task);
        }
        tx.Commit(); return task;
    }

    internal InspectionTask? StartTask(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var current = ReadTask(db, tx, id);
        if (current is null || current.State != "queued" || current.CancelRequested) return null;
        var task = current with { State = "running", ExecutorPid = Environment.ProcessId, Updated = DateTimeOffset.UtcNow };
        Put(db, tx, "inspection-task", id, task); tx.Commit(); return task;
    }

    internal void UpdateTask(InspectionTask task)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var current = ReadTask(db, tx, task.Id) ?? throw new KeyNotFoundException("未找到任务。");
        Put(db, tx, "inspection-task", task.Id, task with {
            CancelRequested = task.CancelRequested || current.CancelRequested, Updated = DateTimeOffset.UtcNow });
        tx.Commit();
    }

    // Called only while holding the execution lock: no other process can own running work.
    internal void RecoverInterruptedTasks()
    {
        foreach (var task in Tasks.Where(t => t.State == "running"))
            UpdateTask(task with { State = "failed", ErrorCode = "executor_interrupted", Error = "执行进程已中断；未自动重试，请重新提交。" });
    }

    internal void PromoteRequests()
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var q = Command(db, tx, "SELECT kind,target FROM requests ORDER BY id"))
        using (var rows = q.ExecuteReader())
        {
            List<(string Kind, string Id)> requests = [];
            while (rows.Read()) requests.Add((rows.GetString(0), rows.GetString(1)));
            rows.Close();
            foreach (var (kind, id) in requests) SubmitTask(db, tx, kind, id, "任务队列");
        }
        using var clear = Command(db, tx, "DELETE FROM requests"); clear.ExecuteNonQuery();
        tx.Commit();
    }

    // FileShare.None is released by Windows even if the executor crashes; no stale PID lease.
    internal async Task<FileStream> ExecutionLock(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryExecutionLock() is FileStream file) return file;
            await Task.Delay(100, token);
        }
    }

    internal FileStream? TryExecutionLock()
    {
        try { return new FileStream(Path.Combine(DirectoryPath, ".execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
}
