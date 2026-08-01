using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DesktopShrine.Plugin.ArtworkPalette;

internal static class ArtworkDecoder
{
    public static async ValueTask<DecodedArtwork> DecodeAsync(byte[] encodedArtwork, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(encodedArtwork);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var alphaMode = decoder.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? BitmapAlphaMode.Premultiplied
            : BitmapAlphaMode.Straight;

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            alphaMode,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        token.ThrowIfCancellationRequested();
        return new(
            pixelData.DetachPixelData(),
            checked((int)decoder.OrientedPixelWidth),
            checked((int)decoder.OrientedPixelHeight),
            alphaMode == BitmapAlphaMode.Premultiplied);
    }
}

internal sealed record DecodedArtwork(
    byte[] Pixels,
    int Width,
    int Height,
    bool IsPremultiplied);
