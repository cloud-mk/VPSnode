using Microsoft.Data.Sqlite;

namespace VPSnode.Core;

public sealed partial class Store
{
    public NodeMatch[] FindDuplicateCandidates()
    {
        var nodes = Nodes; var hosts = Hosts;
        List<NodeMatch> matches = [];
        for (var i = 0; i < nodes.Length; i++)
            for (var j = i + 1; j < nodes.Length; j++)
            {
                var match = NodeIdentity.Compare(nodes[i], nodes[j], hosts);
                if (match.CanMerge) matches.Add(match);
            }
        return [.. matches];
    }

    public int ReconcileNodes()
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var count = ReconcileNodes(db, tx);
        tx.Commit(); return count;
    }

    private static int ReconcileNodes(SqliteConnection db, SqliteTransaction tx)
    {
        var nodes = List<Node>(db, tx, "node").ToList(); var hosts = List<Host>(db, tx, "host");
        int count = 0;
        for (var i = 0; i < nodes.Count; i++)
            for (var j = nodes.Count - 1; j > i; j--)
                if (NodeIdentity.Compare(nodes[i], nodes[j], hosts).Disposition == "Same")
                {
                    nodes[i] = MergeNodes(db, tx, nodes[i], nodes[j]); nodes.RemoveAt(j); count++;
                }
        return count;
    }

    public void MergeNodes(string keepId, string removeId)
    {
        if (keepId == removeId) throw new InvalidDataException("请选择不同记录。");
        using var db = Open(); using var tx = db.BeginTransaction();
        var nodes = List<Node>(db, tx, "node");
        var keep = nodes.Single(n => n.Id == keepId); var remove = nodes.Single(n => n.Id == removeId);
        if (!NodeIdentity.Compare(keep, remove, List<Host>(db, tx, "host")).CanAssociate)
            throw new InvalidDataException("配置存在明确冲突或没有关联依据，请暂缓关联并核对原始配置。");
        MergeNodes(db, tx, keep, remove, confirmed: true); tx.Commit();
    }

    private static Node MergeNodes(SqliteConnection db, SqliteTransaction tx, Node keep, Node remove, bool confirmed = false)
    {
        var merged = NodeIdentity.Merge(keep, remove, associate: true);
        if (confirmed) merged = merged with { Sources = merged.Sources.Select(s => s with { Confirmed = true }).ToArray() };
        Put(db, tx, "node", keep.Id, merged);
        foreach (var job in List<InspectionTask>(db, tx, "inspection-task"))
        {
            var nodeTask = job.Kind is "node" or "speed" or "sni";
            var result = job.Result is null ? null : job.Result with { Items = job.Result.Items.Select(i =>
                i.Id == remove.Id && i.Kind is "node" or "speed" or "sni" ? i with { Id = keep.Id } : i).ToArray() };
            Put(db, tx, "inspection-task", job.Id, job with { TargetId = nodeTask && job.TargetId == remove.Id ? keep.Id : job.TargetId, Result = result });
        }
        foreach (var web in List<WebTarget>(db, tx, "web"))
            if (web.FallbackNodeIds.Contains(remove.Id) || web.CertificateNodeIds.Contains(remove.Id))
                Put(db, tx, "web", web.Id, web with
                {
                    FallbackNodeIds = web.FallbackNodeIds.Select(id => id == remove.Id ? keep.Id : id).Distinct().ToArray(),
                    CertificateNodeIds = web.CertificateNodeIds.Select(id => id == remove.Id ? keep.Id : id).Distinct().ToArray()
                });
        List<Observation> observations = [];
        using (var query = Command(db, tx, "SELECT data FROM observations WHERE id=$i AND kind IN ('node','speed','sni','sni-node','sni-vps','sni-local')", ("$i", remove.Id)))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) observations.Add(Unprotect<Observation>(reader[0]));
        foreach (var observation in observations)
            MergeObservation(db, tx, observation with { Id = keep.Id });
        List<Observation> tasks = [];
        using (var query = Command(db, tx, "SELECT data FROM observations WHERE kind='task' AND id='history'"))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) tasks.Add(Unprotect<Observation>(reader[0]));
        foreach (var task in tasks)
        {
            try
            {
                var inspection = Wire.Decode<Inspection>(task.Detail);
                var items = inspection.Items.Select(item => item.Id == remove.Id &&
                    item.Kind is "node" or "speed" or "sni" or "sni-node" or "sni-vps" or "sni-local"
                    ? item with { Id = keep.Id } : item).ToArray();
                if (items.SequenceEqual(inspection.Items)) continue;
                var revised = task with { Detail = Wire.Encode(inspection with { Items = items }) };
                using var update = Command(db, tx, "UPDATE observations SET data=$d WHERE kind='task' AND id='history' AND time=$t",
                    ("$d", Protect(revised)), ("$t", Stamp(revised.At)));
                update.ExecuteNonQuery();
            }
            catch (System.Text.Json.JsonException) { /* Preserve old task formats verbatim. */ }
        }
        using var cleanup = Command(db, tx, """
            INSERT OR IGNORE INTO requests(kind,target) SELECT kind,$keep FROM requests WHERE target=$remove
                AND kind IN ('node','speed','sni','sni-node','sni-vps','sni-local');
            DELETE FROM requests WHERE target=$remove AND kind IN ('node','speed','sni','sni-node','sni-vps','sni-local');
            DELETE FROM observations WHERE id=$remove AND kind IN ('node','speed','sni','sni-node','sni-vps','sni-local');
            DELETE FROM assets WHERE kind='node' AND id=$remove;
            """, ("$keep", keep.Id), ("$remove", remove.Id));
        cleanup.ExecuteNonQuery(); return merged;
    }

    private static void MergeObservation(SqliteConnection db, SqliteTransaction tx, Observation value)
    {
        // Preserve distinct results at the same timestamp; replay finds the same adjusted slot.
        while (true)
        {
            using (var existing = Command(db, tx, "SELECT data FROM observations WHERE kind=$k AND id=$i AND time=$t",
                ("$k", value.Kind), ("$i", value.Id), ("$t", Stamp(value.At))))
            {
                var data = existing.ExecuteScalar();
                if (data is not null && Unprotect<Observation>(data) == value) return;
            }
            using var insert = Command(db, tx, "INSERT OR IGNORE INTO observations VALUES($k,$i,$t,$d)",
                ("$k", value.Kind), ("$i", value.Id), ("$t", Stamp(value.At)), ("$d", Protect(value)));
            if (insert.ExecuteNonQuery() == 1) return;
            value = value with { At = value.At.AddTicks(1) };
        }
    }
}
