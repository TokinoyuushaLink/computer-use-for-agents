using System.Globalization;

namespace ComputerUse;

/// <summary>Parsed command line. Unknown options are errors, so typos fail fast instead of becoming text.</summary>
internal sealed class Options
{
    public string Command = "";
    public readonly List<string> Args = [];

    // Capture target
    public string? Window, Process, Region;
    public long Hwnd;
    public bool Screen, All;

    // UI Automation selectors
    public string? Name, Id, Type;
    public int Index, Limit;
    public bool Raw, Invoke, ViaKeys, Gone;

    // Output and timing
    public string? Out;
    public string Session = "default";
    public int MaxSide = 1568, Wait = 400, Timeout = 5000, Duration = 300;
    public bool Shot, Abs, DoubleClick, PressEnter, Help;
    public MouseButton Button = MouseButton.Left;

    public bool HasWindowSelector => Window is not null || Process is not null || Hwnd != 0;
    public bool HasUiSelector => Name is not null || Id is not null || Type is not null;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var i = 0;
        if (args.Length > 0 && !IsOption(args[0]))
        {
            o.Command = args[0].ToLowerInvariant();
            i = 1;
        }

        var literal = false; // after "--" everything is positional
        for (; i < args.Length; i++)
        {
            var arg = args[i];
            if (literal || !IsOption(arg))
            {
                o.Args.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                literal = true;
                continue;
            }

            // -name, -name:value or -name=value
            var name = arg[1..];
            string? inline = null;
            var split = name.IndexOfAny([':', '=']);
            if (split >= 0)
            {
                inline = name[(split + 1)..];
                name = name[..split];
            }
            name = name.ToLowerInvariant();

            // These close over `i` and `inline`, so a value option can consume the next argument.
            bool Flag() => inline is null || !inline.Equals("false", StringComparison.OrdinalIgnoreCase);
            string Value() => inline ?? (i + 1 < args.Length ? args[++i] : throw AppError.BadArgs($"-{name} requires a value"));
            int Int(int min, int max) => ParseInt(name, Value(), min, max);

            switch (name)
            {
                case "window": o.Window = Value(); break;
                case "process": o.Process = Value(); break;
                case "hwnd": o.Hwnd = ParseHwnd(Value()); break;
                case "region": o.Region = Value(); break;
                case "screen": o.Screen = Flag(); break;
                case "all": o.All = Flag(); break;

                case "name": o.Name = Value(); break;
                case "id": o.Id = Value(); break;
                case "type": o.Type = Value(); break;
                case "index": o.Index = Int(1, 100_000); break;
                case "limit": o.Limit = Int(0, 100_000); break;
                case "raw": o.Raw = Flag(); break;
                case "invoke": o.Invoke = Flag(); break;
                case "keys": o.ViaKeys = Flag(); break;
                case "gone": o.Gone = Flag(); break;

                case "out": o.Out = Value(); break;
                case "session": o.Session = Value(); break;
                case "maxside": o.MaxSide = Int(0, 20_000); break;
                case "wait": o.Wait = Int(0, 120_000); break;
                case "timeout": o.Timeout = Int(0, 120_000); break;
                case "duration": o.Duration = Int(0, 60_000); break;

                case "shot": o.Shot = Flag(); break;
                case "abs": o.Abs = Flag(); break;
                case "double": o.DoubleClick = Flag(); break;
                case "enter": o.PressEnter = Flag(); break;
                case "button": o.Button = ParseButton(Value()); break;
                case "help":
                case "h": o.Help = Flag(); break;

                default:
                    throw AppError.BadArgs($"unknown option -{name} (put -- before literal text that starts with '-')");
            }
        }
        return o;
    }

    /// <summary>"-5" is a negative coordinate, not an option.</summary>
    private static bool IsOption(string s) =>
        s.Length > 1 && s[0] == '-' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static int ParseInt(string option, string text, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw AppError.BadArgs($"-{option} must be an integer between {min} and {max}");
        return value;
    }

    private static long ParseHwnd(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var ok = long.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value);
        if (!ok || value <= 0)
            throw AppError.BadArgs("-hwnd must be a positive window handle (decimal or 0x hex)");
        return value;
    }

    private static MouseButton ParseButton(string text) => text.ToLowerInvariant() switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => throw AppError.BadArgs("-button must be left, right or middle")
    };
}
