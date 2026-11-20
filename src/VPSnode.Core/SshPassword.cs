namespace VPSnode.Core;

public static class SshPassword
{
    public static int? TryAnswer(string[] args)
    {
        if (Environment.GetEnvironmentVariable("VPSNODE_ASKPASS") is string cipher)
        {
            if (args.Length != 1 || !args[0].Contains("password", StringComparison.OrdinalIgnoreCase)) return 1;
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(cipher), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                try { using var output = Console.OpenStandardOutput(); output.Write(bytes); output.WriteByte(10); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                return 0;
            }
            catch { return 1; }
        }
        return null;
    }
}
