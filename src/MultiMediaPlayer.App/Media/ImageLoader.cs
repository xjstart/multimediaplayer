using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MultiMediaPlayer.App.Media;
public static class ImageLoader
{
    public static (BitmapSource Bitmap, double Aspect) Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("无法解码图片。");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 80_000_000)
            throw new InvalidDataException("图片尺寸无效或超过 8000 万像素。");
        var scale = Math.Min(1d, 4096d / Math.Max(info.Width, info.Height));
        var size = codec.GetScaledDimensions((float)scale);
        using var bitmap = new SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException("图片文件损坏或不完整。");
        int o = (int)codec.EncodedOrigin;
        bool rotated = o >= 5;
        float outputScale = Math.Min(1, 4096f / Math.Max(bitmap.Width, bitmap.Height));
        int targetW = Math.Max(1, (int)Math.Round((rotated ? bitmap.Height : bitmap.Width) * outputScale));
        int targetH = Math.Max(1, (int)Math.Round((rotated ? bitmap.Width : bitmap.Height) * outputScale));
        using var oriented = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(oriented))
        {
            float w = bitmap.Width, h = bitmap.Height;
            var matrix = o switch
            {
                2 => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                3 => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                4 => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                6 => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
                7 => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
                8 => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateScale(outputScale, outputScale), matrix));
            canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }
        var source = BitmapSource.Create(oriented.Width, oriented.Height, 96, 96, PixelFormats.Pbgra32, null,
            oriented.GetPixels(), oriented.ByteCount, oriented.RowBytes);
        source.Freeze();
        var aspect = rotated ? (double)info.Height / info.Width : (double)info.Width / info.Height;
        return (source, aspect);
    }
}
