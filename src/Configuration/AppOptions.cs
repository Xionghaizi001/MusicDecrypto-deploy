namespace MusicDecrypto.Backend;

internal sealed class AppOptions
{
    public string StorageRoot { get; set; } = "data";
    public string TempRoot { get; set; } = "data/tmp";
    public string DecryptoExecutablePath { get; set; } = "package/musicdecrypto";
    public string? ApiKey { get; set; }
    public bool ForceOverwrite { get; set; } = true;
    public bool ExtensiveDetection { get; set; }
}
