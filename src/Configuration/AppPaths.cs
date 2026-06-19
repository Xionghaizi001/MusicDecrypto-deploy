namespace MusicDecrypto.Backend;

internal sealed record AppPaths(string Root, string TempRoot, string TusStore, string Uploads, string Outputs, string State, string Updates, string UpdateApplyRoot)
{
    public static AppPaths From(AppOptions options, string contentRoot)
    {
        var root = Path.GetFullPath(options.StorageRoot, contentRoot);
        var tempRoot = Path.GetFullPath(options.TempRoot, contentRoot);
        var updateRoot = Path.GetFullPath(options.UpdateRoot, contentRoot);
        var updateApplyRoot = Path.GetFullPath(options.UpdateApplyRoot, contentRoot);
        return new AppPaths(
            root,
            tempRoot,
            Path.Combine(tempRoot, "tus"),
            Path.Combine(root, "uploads"),
            Path.Combine(root, "outputs"),
            Path.Combine(root, "state"),
            updateRoot,
            updateApplyRoot);
    }
}
