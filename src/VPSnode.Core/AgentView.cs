using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPSnode.Core;

// One projection for CLI queries; never serialize database credential envelopes to stdout.
public sealed class AgentView(Store store)
{
    public object Host(Host host) => new { asset = host with { PasswordCipher = "" },
        sshPasswordSet = host.PasswordCipher.Length > 0, revision = Store.Revision(host),
        portPlan = new PortPlan(host, store.Nodes, WebProbe.Targets(store)) };

    public object Node(Node node, bool secrets = false)
    {
        ShareLink? link = null;
        try { link = ShareLink.Parse(ProxyProbe.ClientLink(store, node)); } catch (InvalidDataException) { }
        var fields = link?.Fields().Select(f => f with { Value = f.Sensitive && !secrets ? "[redacted]" : f.Value }).ToArray();
        return new { asset = secrets ? node : node with { Link = node.Link.Length > 0 ? "[redacted]" : "", ClientId = "[redacted]",
            Server = SafeDictionary(node.Server), Related = SafeDictionary(node.Related), Sources = NodeIdentity.Sources(node).Select(s => s with {
                Link = s.Link.Length > 0 ? "[redacted]" : "", Configuration = s.Configuration.Length > 0 ? "[redacted]" : "" }).ToArray() },
            revision = Store.Revision(node), clientFields = fields,
            websites = WebProbe.Targets(store).Where(w => w.CertificateNodeIds.Contains(node.Id) || w.FallbackNodeIds.Contains(node.Id) || WebProbe.UsesEndpoint(node, w))
                .Select(w => new { w.Id, certificate = w.CertificateNodeIds.Contains(node.Id), fallback = w.FallbackNodeIds.Contains(node.Id), automaticFallback = w.AutoFallbackNodeIds.Contains(node.Id) }) };
    }

    public static object Web(WebTarget web) => new { asset = web, revision = Store.Revision(web), web.AutoFallbackNodeIds };
    public static JsonObject Observation(Observation value)
    {
        var data = JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
        data["effectiveState"] = ObservationState.Current(value);
        return data;
    }
    private static Dictionary<string, string> SafeDictionary(Dictionary<string, string> fields) =>
        fields.ToDictionary(p => p.Key, p => ShareLink.IsSensitive(p.Key) ? "[redacted]" : p.Value);

    public JsonNode? Safe<T>(T value)
    {
        HashSet<string> secrets = [];
        void AddLink(string value)
        {
            try
            {
                var parsed = ShareLink.Parse(value); secrets.Add(value); secrets.Add(parsed.Auth);
                foreach (var pair in parsed.Options.Where(p => ShareLink.IsSensitive(p.Key) || p.Key == "encryption")) secrets.Add(pair.Value);
            }
            catch (InvalidDataException) { }
        }
        foreach (var node in store.Nodes)
        {
            AddLink(node.Link);
            if (node.ClientId.Length > 0) secrets.Add(node.ClientId);
            foreach (var pair in node.Server.Concat(node.Related).Where(p => ShareLink.IsSensitive(p.Key))) secrets.Add(pair.Value);
            foreach (var source in NodeIdentity.Sources(node)) AddLink(source.Link);
        }
        var output = JsonSerializer.SerializeToNode(value, Wire.Json);
        JsonNode? Scrub(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(p => p.Key).ToArray())
                    obj[key] = ShareLink.IsSensitive(key) && key is not ("sshPasswordSet" or "cancelRequested") ? JsonValue.Create("[redacted]") : Scrub(obj[key]);
            }
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++) array[i] = Scrub(array[i]);
            else if (node is JsonValue val && val.TryGetValue<string>(out var text))
            {
                text = Regex.Replace(text, @"(?:vless|hysteria2|hy2)://[^\s\""<>]+", "[redacted]", RegexOptions.IgnoreCase);
                foreach (var secret in secrets.Where(s => s.Length >= 4).OrderByDescending(s => s.Length)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
                return JsonValue.Create(text);
            }
            return node?.DeepClone();
        }
        return Scrub(output);
    }
}
