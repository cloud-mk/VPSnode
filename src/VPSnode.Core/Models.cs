using System.Text.Json;

namespace VPSnode.Core;

public sealed record Host
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Address { get; init; } = "";
    public string Ipv6 { get; init; } = "";
    public int SshPort { get; init; } = 22;
    public string SshAlias { get; init; } = "";
    public string User { get; init; } = "";
    public string PasswordCipher { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Os { get; init; } = "";
    public string Provider { get; init; } = "";
    public int? TunnelPort { get; init; }
    public int? RemotePanelPort { get; init; }
    public string RemotePanelPath { get; init; } = "";
    public string PanelScheme { get; init; } = "";
    public string PanelPath { get; init; } = "";
    public string Notes { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Source { get; init; } = "手工";
    public DateTimeOffset? InventoriedAt { get; init; }
    public Listener[] Ports { get; init; } = [];
}

public sealed record Listener(string Protocol, string Address, int Port, string Service);

public sealed record Node
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string HostId { get; init; } = "";
    public string Link { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public string Transport { get; init; } = "";
    public string Security { get; init; } = "";
    public string Notes { get; init; } = "";
    public string InboundId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public NodeSource[] Sources { get; init; } = [];
    public bool VpsVerified { get; init; }
    public bool Manual { get; init; }
    public bool Paused { get; init; }
    public bool SourceRemoved { get; init; }
    public string V2Id { get; init; } = "";
    public string V2Directory { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string Source { get; init; } = "手工";
    public string IpMode { get; init; } = "auto";
    public DateTimeOffset? InventoriedAt { get; init; }
    public Dictionary<string, string> Server { get; init; } = [];
    public Dictionary<string, string> Related { get; init; } = [];
}

public sealed record NodeSource(string Kind, string Key, string Directory = "", string GroupId = "",
    string Name = "", string Link = "", bool Removed = false, bool Confirmed = false, string Configuration = "");

public sealed record WebTarget
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string HostId { get; init; } = "";
    public string Url { get; init; } = "";
    public string Domain { get; init; } = "";
    public string ConnectAddress { get; init; } = "";
    public string Sni { get; init; } = "";
    public int Port { get; init; } = 443;
    public string Location { get; init; } = "公网";
    public string[] FallbackNodeIds { get; init; } = [];
    // View-only provenance. These links are recomputed from the latest inventory, never persisted as manual links.
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] AutoFallbackNodeIds { get; init; } = [];
    public string[] CertificateNodeIds { get; init; } = [];
    public bool CheckWebsite { get; init; } = true;
    public int? ExpectedStatus { get; init; }
    public string Contains { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public sealed record Settings
{
    public string V2Directory { get; init; } = "";
    public string Theme { get; init; } = "light";
    public bool ScheduledInspectionEnabled { get; init; } = false;
    public int IntervalMinutes { get; init; } = 15;
    public int HistoryDays { get; init; } = 90;
    public int SpeedBytes { get; init; } = 10_000_000;
    public string PingUrl { get; init; } = "https://www.google.com/generate_204";
    public int PingStatus { get; init; } = 204;
    public string SpeedUrl { get; init; } = "https://speed.cloudflare.com/__down?bytes=10000000";
    public string[] FollowGroups { get; init; } = [];
}

public sealed record Observation(string Kind, string Id, DateTimeOffset At, string State,
    string Detail = "", double? Value = null, string Context = "", string Address = "",
    DateTimeOffset? Expires = null, string Title = "", int? HttpStatus = null, string FinalUrl = "");
public sealed record Request(long Id, string Kind, string TargetId);
public sealed record InventoryDocument(int Version, Host[]? Hosts, Node[]? Nodes, WebTarget[]? Websites);
public sealed record InventorySummary(int Added, int Updated, string[] Conflicts, int Merged = 0);
public sealed record V2Group(string Id, string Name, int Count);
public sealed record SyncSummary(int Added, int Updated, int Paused, int Unsupported, string[] Conflicts);
public sealed record DisplayField(string Name, string Value, bool Sensitive);

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("数据为空。");
}
