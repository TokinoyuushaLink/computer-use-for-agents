using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace ComputerUse;

internal enum TargetMode { Screen, All, Region, Window }

internal sealed record Target(TargetMode Mode, long Hwnd = 0, string? Title = null, int[]? Region = null);

/// <summary>What the latest shot looked like; lets later commands turn image pixels back into screen pixels.</summary>
internal sealed class ShotState
{
    public TargetMode Mode { get; set; }
    public long Hwnd { get; set; }
    public string? Title { get; set; }
    public int[]? Region { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public double Scale { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>One small JSON file per -session in %TEMP%.</summary>
internal static class StateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { Converters = { new JsonStringEnumConverter() } };

    private static string PathFor(Options o)
    {
        var safe = string.Concat(o.Session.Select(c => (char.IsLetterOrDigit(c) || c is '-' or '_') ? c : '_'));
        return Path.Combine(Path.GetTempPath(), $"computerUse_{safe}.json");
    }

    public static ShotState? Load(Options o)
    {
        try { return JsonSerializer.Deserialize<ShotState>(File.ReadAllText(PathFor(o)), JsonOpts); }
        catch { return null; } // missing or corrupt state simply means "no previous shot"
    }

    public static void Save(Options o, ShotState state) =>
        File.WriteAllText(PathFor(o), JsonSerializer.Serialize(state, JsonOpts));
}

internal static class Screenshot
{
    /// <summary>Decide what to capture. With useLast, fall back to the target of the previous shot.</summary>
    public static Target Resolve(Options o, bool useLast = false)
    {
        var selectors = (o.HasWindowSelector ? 1 : 0) + (o.Region is not null ? 1 : 0) + (o.Screen ? 1 : 0) + (o.All ? 1 : 0);
        if (selectors > 1)
            throw AppError.BadArgs("use only one target selector: -window/-process/-hwnd, -region, -screen or -all");

        if (o.HasWindowSelector)
        {
            var window = WindowFinder.Resolve(o);
            return new Target(TargetMode.Window, window.Hwnd.ToInt64(), window.Title);
        }
        if (o.Region is not null)
            return new Target(TargetMode.Region, Region: ParseRegion(o.Region));
        if (o.All)
            return new Target(TargetMode.All);
        if (o.Screen)
            return new Target(TargetMode.Screen);

        if (useLast && StateStore.Load(o) is { } last)
        {
            if (last.Mode == TargetMode.Window && !Native.IsWindow(new IntPtr(last.Hwnd)))
                return new Target(TargetMode.Screen); // the window is gone
            return new Target(last.Mode, last.Hwnd, last.Title, last.Region);
        }
        return new Target(TargetMode.Screen);
    }

    /// <summary>Capture, save the image and remember its origin/scale for later coordinates.</summary>
    public static Result Take(Target target, Options o, bool focus)
    {
        var bounds = CaptureBounds(target, focus);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw AppError.BadArgs($"invalid capture size {bounds.Width}x{bounds.Height}");

        using var full = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(full))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        var longest = Math.Max(bounds.Width, bounds.Height);
        var scale = o.MaxSide > 0 && longest > o.MaxSide ? (double)o.MaxSide / longest : 1.0;
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

        using var scaled = scale < 1 ? Resize(full, width, height) : null;
        var image = scaled ?? full;

        var path = OutputPath(o);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        image.Save(path, IsJpeg(path) ? ImageFormat.Jpeg : ImageFormat.Png);

        StateStore.Save(o, new ShotState
        {
            Mode = target.Mode,
            Hwnd = target.Hwnd,
            Title = target.Title,
            Region = target.Region,
            OriginX = bounds.X,
            OriginY = bounds.Y,
            Scale = scale,
            Width = width,
            Height = height
        });

        return Result.Of(
            ("path", path),
            ("width", width),
            ("height", height),
            ("scale", Math.Round(scale, 4)),
            ("origin", new[] { bounds.X, bounds.Y }),
            ("target", target.Mode.ToString().ToLowerInvariant()),
            ("title", target.Title),
            ("hwnd", target.Hwnd == 0 ? null : (object)target.Hwnd));
    }

    private static Rectangle CaptureBounds(Target target, bool focus)
    {
        switch (target.Mode)
        {
            case TargetMode.Window:
            {
                var hwnd = new IntPtr(target.Hwnd);
                if (!Native.IsWindow(hwnd))
                    throw AppError.NotFound("window no longer exists");
                if (focus)
                {
                    Native.Focus(hwnd);
                    Thread.Sleep(200);
                }
                return Native.GetRect(hwnd);
            }
            case TargetMode.Region:
            {
                var r = target.Region!;
                return new Rectangle(r[0], r[1], r[2], r[3]);
            }
            case TargetMode.All:
                return SystemInformation.VirtualScreen;
            default:
                return Screen.PrimaryScreen?.Bounds ?? throw AppError.Failed("primary display unavailable");
        }
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var resized = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static string OutputPath(Options o)
    {
        if (o.Out is not null)
            return Path.GetFullPath(o.Out);

        DeleteOldShots();
        return Path.Combine(Path.GetTempPath(), $"computerUse_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
    }

    private static bool IsJpeg(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Housekeeping for default-named shots.</summary>
    private static void DeleteOldShots()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "computerUse_*.png"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddHours(-6))
                    File.Delete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int[] ParseRegion(string text)
    {
        var parts = text.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw AppError.BadArgs("-region needs x,y,w,h (integers)");
        return parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    }
}

/// <summary>Image pixels of the latest shot -> screen pixels.</summary>
internal static class Coordinates
{
    public static Point Map(Options o, string x, string y) => Map(o, ParseNumber(x, y), ParseNumber(y, x));

    public static Point Map(Options o, double x, double y)
    {
        if (o.Abs || StateStore.Load(o) is not { } shot)
            return new Point(Round(x), Round(y));

        if (x < 0 || y < 0 || x > shot.Width || y > shot.Height)
        {
            throw AppError.BadArgs(
                $"point ({x:0.#},{y:0.#}) is outside the latest shot ({shot.Width}x{shot.Height}); take a new shot or use -abs");
        }

        var (originX, originY) = (shot.OriginX, shot.OriginY);
        if (shot.Mode == TargetMode.Window && Native.IsWindow(new IntPtr(shot.Hwnd)))
        {
            // Follow the window if it moved since the shot.
            var rect = Native.GetRect(new IntPtr(shot.Hwnd));
            (originX, originY) = (rect.X, rect.Y);
        }
        return new Point(Round(originX + x / shot.Scale), Round(originY + y / shot.Scale));
    }

    /// <summary>Center of the latest shot in screen pixels, or null if there is none.</summary>
    public static Point? LastShotCenter(Options o) =>
        StateStore.Load(o) is { } shot ? Map(o, shot.Width / 2.0, shot.Height / 2.0) : null;

    private static double ParseNumber(string text, string other)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw AppError.BadArgs($"bad coordinate: {text} {other}");
        return value;
    }

    private static int Round(double value) => (int)Math.Round(value);
}
