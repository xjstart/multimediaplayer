using System.IO;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Media;

internal sealed record ImageDirectoryResult(List<string> Paths, List<string> Errors);

internal static class ImageDirectory
{
    public static Task<ImageDirectoryResult> ReadAsync(string directory, bool recursive = false, CancellationToken token = default) => Task.Run(() =>
    {
        var paths = new List<string>(); var errors = new List<string>(); var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            try
            {
                foreach (string path in Directory.EnumerateFiles(current))
                {
                    token.ThrowIfCancellationRequested();
                    if (MediaFiles.Images.Contains(Path.GetExtension(path))) paths.Add(Path.GetFullPath(path));
                }
                if (recursive)
                    foreach (string child in Directory.EnumerateDirectories(current))
                    {
                        token.ThrowIfCancellationRequested();
                        try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{child}：{ex.Message}"); }
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{current}：{ex.Message}"); }
        }
        return new ImageDirectoryResult(ImageSequence.Distinct(paths).Order(ImageSequence.NaturalOrder).ToList(), errors);
    }, token);
}
