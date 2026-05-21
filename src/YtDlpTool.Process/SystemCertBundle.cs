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
/// Narrowed in v1.2.6 to read only CurrentUser\Trusted Root Certification
/// Authorities — previous versions also scanned LocalMachine\Root and both
/// CertificateAuthority stores, but on networks with HTTPS monitoring the
/// extra stores can introduce trust anchors that the monitor doesn't expect.
/// Reading exactly the store the user manages locally keeps yt-dlp's trust
/// view consistent with what the monitor expects.
/// </summary>
public static class SystemCertBundle
{
    /// <summary>
    /// Generates or overwrites a PEM CA bundle at <paramref name="outputPath"/>.
    /// Returns true on success. When <paramref name="logger"/> is supplied,
    /// emits a `ca-bundle.entries` event listing the SHA-1 thumbprint of every
    /// exported certificate — no subjects, no issuer names, so the log is safe
    /// to share. The user can cross-reference thumbprints against certmgr.msc
    /// to confirm the expected trust anchor is included.
    /// </summary>
    public static bool GenerateOrRefresh(string outputPath, AppLogger? logger = null)
    {
        try
        {
            var sb = new StringBuilder(capacity: 64 * 1024);
            var count = 0;

            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
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

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.ASCII);

            // v1.2.7: log only the count. Per-cert thumbprints are available
            // on-demand via Settings → 進階 → 檢視已注入的根 CA 指紋, which
            // shows them in a dialog that closes without writing to disk —
            // avoids persistently exposing identifiable fingerprints.
            logger?.Info("ca-bundle.entries", new Dictionary<string, string>
            {
                ["store"] = "CurrentUser\\Root",
                ["count"] = count.ToString(),
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns SHA-1 thumbprints of every root CA currently in
    /// CurrentUser\Trusted Root Certification Authorities. Called from the
    /// Settings dialog when the user clicks the diagnostic button. Never
    /// writes the returned data anywhere — the caller shows them in a
    /// MessageBox that vanishes on close.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledRootThumbprints()
    {
        var thumbprints = new List<string>();
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                if (!string.IsNullOrEmpty(cert.Thumbprint))
                    thumbprints.Add(cert.Thumbprint);
            }
        }
        catch
        {
            // best-effort; return whatever we collected before the failure
        }
        return thumbprints;
    }
}
