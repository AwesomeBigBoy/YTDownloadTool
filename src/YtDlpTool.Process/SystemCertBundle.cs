using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using YtDlpTool.Domain.Logging;

namespace YtDlpTool.Process;

/// <summary>
/// Exports the Windows certificate trust store as a PEM bundle that yt-dlp's
/// Python (urllib / requests / curl) can consume via SSL_CERT_FILE /
/// REQUESTS_CA_BUNDLE / CURL_CA_BUNDLE.
///
/// v1.3.0-alpha4: scans both LocalMachine\Root AND CurrentUser\Root. GPO-
/// deployed corporate CAs typically land in LocalMachine\Root (system-wide),
/// while certmgr.msc's "Current User → Trusted Root Certification Authorities"
/// view is actually a merged view of both stores. v1.2.6/v1.2.7 narrowed to
/// CurrentUser\Root only, which dropped GPO-installed roots and caused
/// "self-signed certificate in certificate chain" errors during HTTPS
/// validation. Two-store read restores compatibility while still excluding
/// the CertificateAuthority store (intermediate CAs that don't need to be
/// trust anchors).
/// </summary>
public static class SystemCertBundle
{
    /// <summary>
    /// Generates or overwrites a PEM CA bundle at <paramref name="outputPath"/>.
    /// Returns true on success. When <paramref name="logger"/> is supplied,
    /// emits a `ca-bundle.entries` event with the cert count per store — no
    /// thumbprints, no subjects, no issuer names. Per-cert thumbprints are
    /// available on-demand via Settings → 進階 → 檢視已注入的根 CA 指紋,
    /// which shows them in a dialog that closes without writing to disk.
    /// </summary>
    public static bool GenerateOrRefresh(string outputPath, AppLogger? logger = null)
    {
        try
        {
            var sb = new StringBuilder(capacity: 64 * 1024);
            var localMachineCount = AppendStore(sb, StoreName.Root, StoreLocation.LocalMachine);
            var currentUserCount  = AppendStore(sb, StoreName.Root, StoreLocation.CurrentUser);

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.ASCII);

            logger?.Info("ca-bundle.entries", new Dictionary<string, string>
            {
                ["local_machine_count"] = localMachineCount.ToString(),
                ["current_user_count"]  = currentUserCount.ToString(),
                ["total_count"]         = (localMachineCount + currentUserCount).ToString(),
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int AppendStore(StringBuilder sb, StoreName name, StoreLocation location)
    {
        var count = 0;
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                sb.Append("-----BEGIN CERTIFICATE-----\n");
                var b64 = System.Convert.ToBase64String(cert.RawData);
                for (int i = 0; i < b64.Length; i += 64)
                {
                    var take = System.Math.Min(64, b64.Length - i);
                    sb.Append(b64, i, take).Append('\n');
                }
                sb.Append("-----END CERTIFICATE-----\n");
                count++;
            }
        }
        catch
        {
            // Per-store best-effort: missing or restricted stores don't abort
            // the whole bundle. Caller still gets the certs from other stores.
        }
        return count;
    }

    /// <summary>
    /// Writes a minimal OpenSSL config that lowers SECLEVEL to 0 (used as
    /// OPENSSL_CONF). Best-effort defence-in-depth: Python builds with
    /// OPENSSL_INIT_NO_LOAD_CONFIG ignore this, but some alternate OpenSSL
    /// builds (or our patched yt-dlp.exe) may honour it. The actual
    /// handshake-time SECLEVEL relaxation comes from the PyInstaller
    /// runtime hook inside our patched yt-dlp.exe.
    /// </summary>
    public static bool WritePermissiveOpensslConf(string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            const string content =
                "openssl_conf = default_conf\n" +
                "\n" +
                "[default_conf]\n" +
                "ssl_conf = ssl_sect\n" +
                "\n" +
                "[ssl_sect]\n" +
                "system_default = system_default_sect\n" +
                "\n" +
                "[system_default_sect]\n" +
                "CipherString = DEFAULT@SECLEVEL=0\n";
            File.WriteAllText(outputPath, content, Encoding.ASCII);
            return File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns SHA-1 thumbprints of every root CA in BOTH LocalMachine\Root
    /// and CurrentUser\Trusted Root Certification Authorities, tagged with
    /// which store they came from. Called from the Settings dialog when the
    /// user clicks the diagnostic button. Never writes the returned data
    /// anywhere — the caller shows them in a MessageBox that vanishes on close.
    /// </summary>
    public static IReadOnlyList<(string Store, string Thumbprint)> GetInstalledRootThumbprintsWithStore()
    {
        var thumbprints = new List<(string, string)>();
        AppendThumbprints(thumbprints, "LocalMachine\\Root", StoreName.Root, StoreLocation.LocalMachine);
        AppendThumbprints(thumbprints, "CurrentUser\\Root",  StoreName.Root, StoreLocation.CurrentUser);
        return thumbprints;
    }

    /// <summary>
    /// Backward-compat wrapper that returns just thumbprints (no store info).
    /// Preserved so the Settings dialog can keep its old API; new callers
    /// should prefer GetInstalledRootThumbprintsWithStore.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledRootThumbprints()
    {
        return GetInstalledRootThumbprintsWithStore().Select(t => t.Thumbprint).ToList();
    }

    private static void AppendThumbprints(
        List<(string, string)> output, string storeLabel,
        StoreName name, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                if (!string.IsNullOrEmpty(cert.Thumbprint))
                    output.Add((storeLabel, cert.Thumbprint));
            }
        }
        catch
        {
            // best-effort; skip restricted/missing stores
        }
    }
}
