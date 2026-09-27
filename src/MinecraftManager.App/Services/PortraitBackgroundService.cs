using System.Reflection;
using SkiaSharp;
using MinecraftManager.Core.Persistence;

namespace MinecraftManager.App.Services;

public sealed class PortraitBackgroundService(IApplicationPaths paths)
{
    private const int CellSize = 512;
    private const int Columns = 2;
    private const int Rows = 10;
    private const int Spacing = 2;
    public const string ResourceName = "MinecraftManager.App.Assets.Portraits.png";

    public string EnsurePortraitCached(Random random)
    {
        Directory.CreateDirectory(paths.CacheDirectory);
        var index = random.Next(Columns * Rows);
        var row = index / Columns;
        var column = index % Columns;
        var destination = Path.Combine(paths.CacheDirectory, $"portrait-{index}.png");
        if (File.Exists(destination)) return destination;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Portrait resource is missing.");
        using var source = SKBitmap.Decode(stream) ?? throw new InvalidOperationException("Portrait image could not be decoded.");
        var x = column * (CellSize + Spacing);
        var y = row * (CellSize + Spacing);
        using var surface = SKSurface.Create(new SKImageInfo(CellSize, CellSize));
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawBitmap(source, new SKRect(x, y, x + CellSize, y + CellSize),
            new SKRect(0, 0, CellSize, CellSize), paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(destination);
        data.SaveTo(output);
        return destination;
    }
}
