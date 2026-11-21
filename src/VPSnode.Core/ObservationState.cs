namespace VPSnode.Core;

public static class ObservationState
{
    // Observation timestamps remain factual. Only the certificate's own validity elapses.
    public static string Current(Observation value, DateTimeOffset? now = null)
    {
        if (value.Kind == "tls" && value.Expires is DateTimeOffset expires)
        {
            var remaining = expires - (now ?? DateTimeOffset.Now);
            if (remaining <= TimeSpan.Zero) return "证书已过期";
            if (remaining.TotalDays <= 30 && value.State == "TLS 正常") return "即将到期";
        }
        return value.State == "已过期" && value.Kind == "tls" ? "证书已过期" : value.State;
    }
}
