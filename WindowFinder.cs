using System.Text.RegularExpressions;

namespace ComputerUse;

/// <summary>Selects top-level windows by -window (title), -process or -hwnd.</summary>
internal static class WindowFinder
{
    /// <summary>All matching windows, visible ones first. Never throws for "too many".</summary>
    public static List<WindowInfo> Find(Options o)
    {
        var titleMatches = o.Window is null ? null : TitleMatcher(o.Window);
        var processMatches = o.Process is null ? null : GlobMatcher(StripExe(o.Process));

        var found = Native.EnumerateWindows()
            .Where(w => o.Hwnd == 0 || w.Hwnd.ToInt64() == o.Hwnd)
            .Where(w => processMatches?.Invoke(w.Process) ?? true)
            .Where(w => titleMatches?.Invoke(w.Title) ?? true)
            .OrderBy(w => Native.IsIconic(w.Hwnd))
            .ToList();

        // A handle can point at a window we skip while enumerating (untitled, hidden).
        if (found.Count == 0 && o.Hwnd != 0 && Native.IsWindow(new IntPtr(o.Hwnd)))
        {
            var handle = new IntPtr(o.Hwnd);
            found.Add(Native.Describe(handle) ?? new WindowInfo(handle, 0, "", ""));
        }
        return found;
    }

    /// <summary>Exactly one window, or an error that lists the candidates.</summary>
    public static WindowInfo Resolve(Options o)
    {
        var matches = Find(o);
        if (matches.Count == 0)
            throw AppError.NotFound($"no window matches ({Selector(o)})");
        if (matches.Count == 1)
            return matches[0];

        // An exact title match beats substring matches.
        if (o.Window is not null)
        {
            var exact = matches.Where(w => w.Title.Equals(o.Window, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];
        }

        var candidates = string.Join("; ", matches.Take(10).Select(w => $"'{w.Title}' [{w.Process}, hwnd={w.Hwnd.ToInt64()}]"));
        throw AppError.BadArgs($"window selector matched {matches.Count} windows; refine -window/-process or pass -hwnd. Candidates: {candidates}");
    }

    private static string Selector(Options o) => $"process='{o.Process}' window='{o.Window}' hwnd={o.Hwnd}";

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Plain text is a case-insensitive substring; text with * or ? is a wildcard over the whole title.</summary>
    private static Func<string, bool> TitleMatcher(string filter)
    {
        if (filter.Contains('*') || filter.Contains('?'))
            return GlobMatcher("*" + filter + "*");
        return title => title.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static Func<string, bool> GlobMatcher(string pattern)
    {
        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }
}
