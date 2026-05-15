using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace YtDlpTool.Process;

/// <summary>
/// Exports the Windows certificate trust store as a PEM bundle that Python's
/// urllib (and therefore yt-dlp) can consume via SSL_CERT_FILE.
///
/// Why: managed environments commonly run an SSL-inspection proxy (an HTTPS inspection product / an HTTPS inspection product /
/// an HTTPS inspection product / web gateway) that re-signs HTTPS traffic with an enterprise
/// CA. That CA is installed in Windows' root store via GPO so browsers trust it —
/// but yt-dlp's bundled Python certifi has its own CA list that doesn't include
/// the site-installed CA, so TLS handshake fails silently and the metadata fetch hangs
/// until our 30s timeout. Symptom: browser plays YouTube fine, CLI yt-dlp times
/// out with empty stderr.
///
/// Fix: dump LocalMachine\Root + CurrentUser\Root + their CertificateAuthority
/// counterparts into a PEM file at startup. Point SSL_CERT_FILE at it. yt-dlp's
/// Python then trusts everything Windows trusts, including the site-installed CA.
/// </summary>
public static class SystemCertBundle
{
    /// <summary>
    /// Generates or overwrites a PEM CA bundle at <paramref name="outputPath"/>.
    /// Returns true on success. Best-effort: per-store failures (access denied,
    /// missing store) are swallowed so a partial bundle still gets written.
    /// </summary>
    public static bool GenerateOrRefresh(string outputPath)
    {
        try
        {
            var sb = new StringBuilder(capacity: 64 * 1024);
            AppendStore(sb, StoreName.Root, StoreLocation.LocalMachine);
            AppendStore(sb, StoreName.Root, StoreLocation.CurrentUser);
            AppendStore(sb, StoreName.CertificateAuthority, StoreLocation.LocalMachine);
            AppendStore(sb, StoreName.CertificateAuthority, StoreLocation.CurrentUser);

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.ASCII);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendStore(StringBuilder sb, StoreName name, StoreLocation location)
    {
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
            }
        }
        catch
        {
            // Per-store best-effort: some stores are restricted in managed environments;
            // skip them silently rather than aborting the whole bundle.
        }
    }
}
