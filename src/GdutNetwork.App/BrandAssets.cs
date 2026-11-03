namespace GdutNetwork.App;

internal static class BrandAssets
{
    public static Icon LoadIcon()
    {
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream("GdutNetwork.BrandIcon")
            ?? throw new InvalidOperationException("Application icon is missing.");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
