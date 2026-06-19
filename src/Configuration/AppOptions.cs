namespace MusicDecrypto.Backend;

internal sealed class AppOptions
{
    public string StorageRoot { get; set; } = "data";
    public string TempRoot { get; set; } = "data/tmp";
    public string UpdateRoot { get; set; } = "data/updates";
    public string UpdateApplyRoot { get; set; } = ".";
    public string DecryptoExecutablePath { get; set; } = "package/musicdecrypto";
    public string? ApiKey { get; set; }
    public bool ForceOverwrite { get; set; } = true;
    public bool ExtensiveDetection { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
}
