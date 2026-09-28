using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace ComputerUse;

/// <summary>One method per command. Each returns the fields of the JSON response.</summary>
internal static class Commands
{
    // ---- windows and screenshots ---------------------------------------------------------------

    public static Result ListWindows(Options o)
    {
        var windows = WindowFinder.Find(o).Select(w =>
        {
            var r = Native.GetRect(w.Hwnd);
            return new
            {
                pid = w.Pid,
                process = w.Process,
                title = w.Title,
                hwnd = w.Hwnd.ToInt64(),
                rect = new[] { r.X, r.Y, r.Width, r.Height },
                minimized = Native.IsIconic(w.Hwnd)
            };
        }).ToArray();
        return Result.Of(("windows", windows));
    }

    public static Result TakeShot(Options o) =>
        Screenshot.Take(Screenshot.Resolve(o), o, focus: true);

    public static Result FocusWindow(Options o)
    {
        if (!o.HasWindowSelector)
            throw AppError.BadArgs("focus needs -window, -process or -hwnd");

        var window = WindowFinder.Resolve(o);
        Native.Focus(window.Hwnd);
        Thread.Sleep(150);
        return Result.Of(("action", "focus"), ("title", window.Title), ("hwnd", window.Hwnd.ToInt64()));
    }

    // ---- mouse ---------------------------------------------------------------------------------

    public static Result Click(Options o)
    {
        var count = o.DoubleClick ? 2 : 1;

        if (o.HasUiSelector)
        {
            var top = Uia.ResolveTop(o);
            var item = Uia.Pick(Uia.Find(top, o), o);

            string via;
            if (!o.Invoke && item.HasBounds)
            {
                Native.Focus(new IntPtr(top.Hwnd));
                Thread.Sleep(120);
                Native.MoveCursor(item.Center);
                Thread.Sleep(40);
                Native.Click(o.Button, count);
                via = "mouse";
            }
            else
            {
                via = Uia.Invoke(item);
            }

            return Result.Of(
                ("action", "click"),
                ("via", via),
                ("element", Uia.Summary(item)),
                ("screen", new[] { item.Center.X, item.Center.Y }));
        }

        Point? at = null;
        if (o.Args.Count >= 2)
        {
            at = Coordinates.Map(o, o.Args[0], o.Args[1]);
            Native.MoveCursor(at.Value);
            Thread.Sleep(40);
        }
        Native.Click(o.Button, count);
        return Result.Of(
            ("action", "click"),
            ("button", o.Button.ToString().ToLowerInvariant()),
            ("double", o.DoubleClick),
            ("screen", ToArray(at)));
    }

    public static Result Move(Options o)
    {
        RequireArgs(o, 2, "move x y");
        var p = Coordinates.Map(o, o.Args[0], o.Args[1]);
        Native.MoveCursor(p);
        return Result.Of(("action", "move"), ("screen", new[] { p.X, p.Y }));
    }

    public static Result Drag(Options o)
    {
        RequireArgs(o, 4, "drag x1 y1 x2 y2 [-duration ms]");
        var start = Coordinates.Map(o, o.Args[0], o.Args[1]);
        var end = Coordinates.Map(o, o.Args[2], o.Args[3]);

        // Move in small steps: many apps ignore a drag that jumps straight to the target.
        var steps = o.Duration <= 0 ? 1 : Math.Clamp((int)Math.Ceiling(o.Duration / 16.0), 2, 120);
        var delay = o.Duration <= 0 ? 0 : Math.Max(1, o.Duration / steps);

        Native.MoveCursor(start);
        Thread.Sleep(60);
        Native.MouseDown(o.Button);
        Thread.Sleep(60);
        for (var i = 1; i <= steps; i++)
        {
            Native.MoveCursor(new Point(start.X + (end.X - start.X) * i / steps, start.Y + (end.Y - start.Y) * i / steps));
            if (delay > 0)
                Thread.Sleep(delay);
        }
        Thread.Sleep(60);
        Native.MouseUp(o.Button);

        return Result.Of(
            ("action", "drag"),
            ("from", new[] { start.X, start.Y }),
            ("to", new[] { end.X, end.Y }),
            ("durationMs", o.Duration),
            ("steps", steps));
    }

    public static Result Scroll(Options o)
    {
        const string usage = "scroll up|down [n] [x y]";
        RequireArgs(o, 1, usage);

        var direction = o.Args[0].ToLowerInvariant();
        if (direction is not ("up" or "down"))
            throw AppError.BadArgs("usage: computerUse " + usage);

        var amount = 3;
        if (o.Args.Count > 1 && !int.TryParse(o.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
            throw AppError.BadArgs("usage: computerUse " + usage);

        // Wheel events go to whatever is under the cursor, so default to the middle of the last shot.
        Point? at = null;
        if (o.Args.Count >= 4)
            at = Coordinates.Map(o, o.Args[2], o.Args[3]);
        else if (!o.Abs)
            at = Coordinates.LastShotCenter(o);

        if (at is { } p)
        {
            Native.MoveCursor(p);
            Thread.Sleep(40);
        }
        Native.Wheel(direction == "up" ? amount : -amount);

        return Result.Of(("action", "scroll"), ("direction", direction), ("amount", amount), ("screen", ToArray(at)));
    }

    // ---- keyboard ------------------------------------------------------------------------------

    public static Result TypeText(Options o)
    {
        var text = string.Join(' ', o.Args);
        if (text.Length == 0)
            throw AppError.BadArgs("usage: computerUse type <text> [-enter] [-name <text> | -id <id>]");

        if (o.Name is not null || o.Id is not null)
        {
            var top = Uia.ResolveTop(o);
            Uia.FocusItem(top, Uia.Pick(Uia.Find(top, o), o));
        }

        Native.TypeText(text);
        if (o.PressEnter)
            SendEnter();

        return Result.Of(("action", "type"), ("chars", text.Length), ("enter", o.PressEnter));
    }

    public static Result PressKeys(Options o)
    {
        RequireArgs(o, 1, "key <chord> [<chord> ...]");
        foreach (var chord in o.Args)
        {
            Native.Chord(KeyNames.ParseChord(chord));
            Thread.Sleep(40);
        }
        return Result.Of(("action", "key"), ("keys", o.Args.ToArray()));
    }

    // ---- UI Automation -------------------------------------------------------------------------

    public static Result ListControls(Options o)
    {
        var top = Uia.ResolveTop(o);
        var all = Uia.Find(top, o);
        var items = all.Take(o.Limit > 0 ? o.Limit : 150).Select((item, i) => Uia.ToJson(item, i + 1)).ToArray();
        return Result.Of(("window", top.Title), ("count", items.Length), ("total", all.Count), ("items", items));
    }

    public static Result SetValue(Options o)
    {
        RequireSelector(o, "set -name <text> | -id <id> <value> [-enter] [-keys]");
        var text = string.Join(' ', o.Args);
        var top = Uia.ResolveTop(o);
        var item = Uia.Pick(Uia.Find(top, o), o);

        string via;
        if (!o.ViaKeys && Uia.TrySetValue(item, text))
        {
            via = "value";
            if (o.PressEnter)
                Uia.FocusItem(top, item); // Enter goes to the focused control
        }
        else
        {
            // Universal fallback: focus, select all, then type (or delete when the new value is empty).
            Uia.FocusItem(top, item);
            Native.Chord(Native.VkControl, Native.VkA);
            Thread.Sleep(60);
            if (text.Length > 0)
                Native.TypeText(text);
            else
                Native.Chord(Native.VkBack);
            via = "keys";
        }

        if (o.PressEnter)
            SendEnter();

        return Result.Of(("action", "set"), ("via", via), ("element", Uia.Summary(item)), ("chars", text.Length));
    }

    public static Result ReadText(Options o)
    {
        RequireSelector(o, "text -name <text> | -id <id> | -type document [-limit n]");
        var top = Uia.ResolveTop(o);
        var item = Uia.Pick(Uia.Find(top, o), o);

        var limit = o.Limit > 0 ? o.Limit : 4000;
        var (text, via) = Uia.ReadText(item, limit);
        var truncated = text.Length > limit;
        if (truncated)
            text = text[..limit];

        return Result.Of(("via", via), ("chars", text.Length), ("truncated", truncated), ("text", text));
    }

    public static Result WaitFor(Options o)
    {
        RequireSelector(o, "wait -name <text> | -id <id> | -type <t> [-timeout ms] [-gone]");
        var top = Uia.ResolveTop(o);
        var clock = Stopwatch.StartNew();

        List<UiItem> matches;
        while (true)
        {
            try
            {
                matches = Uia.Find(top, o);
            }
            catch (AppError) when (o.Gone)
            {
                matches = []; // the window itself disappeared, which also counts as "gone"
            }

            if (o.Gone ? matches.Count == 0 : matches.Count > 0)
                break;
            if (clock.ElapsedMilliseconds >= o.Timeout)
            {
                throw AppError.NotFound(
                    $"timed out after {o.Timeout}ms waiting for element to be {(o.Gone ? "gone" : "present")} {Uia.SelectorText(o)} in '{top.Title}'");
            }
            Thread.Sleep(150);
        }

        return Result.Of(
            ("action", "wait"),
            ("window", top.Title),
            ("condition", o.Gone ? "gone" : "present"),
            ("elapsedMs", clock.ElapsedMilliseconds),
            ("count", matches.Count),
            ("items", matches.Take(8).Select((item, i) => Uia.ToJson(item, i + 1)).ToArray()));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void RequireArgs(Options o, int count, string usage)
    {
        if (o.Args.Count < count)
            throw AppError.BadArgs("usage: computerUse " + usage);
    }

    private static void RequireSelector(Options o, string usage)
    {
        if (!o.HasUiSelector)
            throw AppError.BadArgs("usage: computerUse " + usage);
    }

    private static void SendEnter()
    {
        Thread.Sleep(30);
        Native.Chord(Native.VkReturn);
    }

    private static int[]? ToArray(Point? p) => p is { } point ? new[] { point.X, point.Y } : null;
}
