namespace YtDlpTool.Interop;

public sealed record WindowsVersionInfo(int Major, int Build)
{
    public bool IsWin11OrLater => Major >= 10 && Build >= 22000;
    public bool SupportsAcrylic => Major >= 10 && Build >= 17763; // 1809
    public bool SupportsMica => IsWin11OrLater;
}

public static class WindowsVersion
{
    public static WindowsVersionInfo Current { get; } = ResolveCurrent();

    private static WindowsVersionInfo ResolveCurrent()
    {
        var os = Environment.OSVersion.Version;
        return new WindowsVersionInfo(os.Major, os.Build);
    }
}
