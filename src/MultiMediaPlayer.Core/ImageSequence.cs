namespace MultiMediaPlayer.Core;

/// <summary>Path-only operations; never opens, decodes or deletes media.</summary>
public static class ImageSequence
{
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    public static readonly IComparer<string> NaturalOrder = Comparer<string>.Create((a, b) =>
    {
        int result = CompareNatural(Path.GetFileName(a), Path.GetFileName(b));
        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(a, b);
    });

    public static string Normalize(string path, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new InvalidDataException("只支持本地媒体文件。");
        return baseDirectory == null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
    }

    public static List<string> Distinct(IEnumerable<string> paths, string? baseDirectory = null) =>
        paths.Select(p => Normalize(p, baseDirectory)).Distinct(PathComparer).ToList();

    public static List<string> Append(IEnumerable<string> existing, IEnumerable<string> added) =>
        Distinct(existing.Concat(added.Order(NaturalOrder)));

    public static int IndexOf(IReadOnlyList<string> paths, string current)
    {
        for (int i = 0; i < paths.Count; i++) if (PathComparer.Equals(paths[i], current)) return i;
        return -1;
    }

    public static IEnumerable<string> Navigate(IReadOnlyList<string> paths, string current, int direction)
    {
        if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int index = IndexOf(paths, current);
        if (index < 0) yield break;
        for (int i = index + direction; i >= 0 && i < paths.Count; i += direction) yield return paths[i];
    }

    public static List<string> ApplyCandidates(IReadOnlyList<string> original, IReadOnlyList<string> edited, string current)
    {
        if (IndexOf(edited, current) >= 0) return [current, .. edited.Where(p => !PathComparer.Equals(p, current))];
        int index = IndexOf(original, current);
        var retained = edited.ToHashSet(PathComparer);
        return original.Skip(index + 1).Concat(original.Take(Math.Max(0, index)).Reverse())
            .Where(retained.Contains).Concat(edited).Distinct(PathComparer).ToList();
    }

    public static List<string> Remove(IReadOnlyList<string> paths, IEnumerable<string> selected)
    {
        var removed = selected.ToHashSet(PathComparer);
        var result = paths.Where(p => !removed.Contains(p)).ToList();
        if (result.Count == 0) throw new InvalidOperationException("至少保留一张图片；删除整个画面请使用“移除画面”。");
        return result;
    }

    public static List<string> Move(IReadOnlyList<string> paths, IEnumerable<string> selected, int direction)
    {
        if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
        var result = paths.ToList(); var selection = selected.ToHashSet(PathComparer);
        if (direction < 0)
        {
            for (int i = 1; i < result.Count; i++)
                if (selection.Contains(result[i]) && !selection.Contains(result[i - 1]))
                    (result[i - 1], result[i]) = (result[i], result[i - 1]);
        }
        else
        {
            for (int i = result.Count - 2; i >= 0; i--)
                if (selection.Contains(result[i]) && !selection.Contains(result[i + 1]))
                    (result[i + 1], result[i]) = (result[i], result[i + 1]);
        }
        return result;
    }

    private static int CompareNatural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                int numberA = startA, numberB = startB;
                while (numberA < i && a[numberA] == '0') numberA++;
                while (numberB < j && b[numberB] == '0') numberB++;
                int result = (i - numberA).CompareTo(j - numberB);
                if (result == 0) result = a.AsSpan(numberA, i - numberA).CompareTo(b.AsSpan(numberB, j - numberB), StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
            }
            else
            {
                int result = char.ToUpperInvariant(a[i++]).CompareTo(char.ToUpperInvariant(b[j++]));
                if (result != 0) return result;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
