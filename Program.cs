using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ComputerUse;

/// <summary>Insertion-ordered JSON object. <see cref="Of"/> drops null values.</summary>
internal sealed class Result : Dictionary<string, object?>
{
    public static Result Of(params (string Key, object? Value)[] pairs)
    {
        var result = new Result();
        foreach (var (key, value) in pairs)
        {
            if (value is not null)
                result[key] = value;
        }
        return result;
    }
}

/// <summary>Expected failure. The exit code tells the caller what kind: 1 bad arguments, 2 not found, 3 failed.</summary>
internal sealed class AppError : Exception
{
    private AppError(int code, string message) : base(message) => Code = code;

    public int Code { get; }

    public static AppError BadArgs(string message) => new(1, message);
    public static AppError NotFound(string message) => new(2, message);
    public static AppError Failed(string message) => new(3, message);
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // keep CJK readable instead of \uXXXX
    };

    /// <summary>Commands that change the screen; -shot re-captures after them.</summary>
    private static readonly HashSet<string> ActionCommands =
        ["focus", "click", "move", "drag", "scroll", "type", "key", "set", "wait"];

    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var options = Options.Parse(args);
            if (options.Help || options.Command.Length == 0)
            {
                Console.WriteLine(HelpText.Value);
                return 0;
            }

            var result = Dispatch(options);
            if (options.Shot && ActionCommands.Contains(options.Command))
            {
                Thread.Sleep(options.Wait);
                result["shot"] = Screenshot.Take(Screenshot.Resolve(options, useLast: true), options, focus: false);
            }

            Emit(ok: true, result);
            return 0;
        }
        catch (AppError e)
        {
            Emit(ok: false, Result.Of(("error", e.Message)));
            return e.Code;
        }
        catch (Exception e)
        {
            Emit(ok: false, Result.Of(("error", $"{e.GetType().Name}: {e.Message}")));
            return 3;
        }
    }

    private static Result Dispatch(Options o) => o.Command switch
    {
        "windows" => Commands.ListWindows(o),
        "shot" => Commands.TakeShot(o),
        "focus" => Commands.FocusWindow(o),
        "click" => Commands.Click(o),
        "move" => Commands.Move(o),
        "drag" => Commands.Drag(o),
        "scroll" => Commands.Scroll(o),
        "type" => Commands.TypeText(o),
        "key" => Commands.PressKeys(o),
        "ui" => Commands.ListControls(o),
        "set" => Commands.SetValue(o),
        "text" => Commands.ReadText(o),
        "wait" => Commands.WaitFor(o),
        _ => throw AppError.BadArgs($"unknown command: {o.Command} (run: computerUse -h)")
    };

    /// <summary>Every response is one JSON object with "ok" first.</summary>
    private static void Emit(bool ok, Result body)
    {
        var output = new Result { ["ok"] = ok };
        foreach (var (key, value) in body)
            output[key] = value;
        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }
}

internal static class HelpText
{
    public const string Value = """
        computerUse - Windows desktop control for CLI agents.
        Normal commands print one JSON line with an "ok" field. -h / -help prints this text.

        COMMANDS
          shot     [-screen | -all | -window <title> | -process <name> | -hwnd <n> | -region x,y,w,h]
          windows  [-window <title>] [-process <name>] [-hwnd <n>]      list top-level windows
          focus    -window <title> | -process <name> | -hwnd <n>
          click    [x y] [-button left|right|middle] [-double]
          move     x y
          drag     x1 y1 x2 y2 [-duration ms]
          scroll   up|down [n] [x y]
          type     <text> [-enter] [-name <text> | -id <id>]
          key      <chord> [<chord> ...]                                e.g. ctrl+s  alt+f4  enter

        UI AUTOMATION (the control tree; more reliable than coordinates for standard controls)
          ui       [-name <text>] [-id <id>] [-type button,edit] [-raw] [-limit n]
          click    -name <text> | -id <id> [-type t] [-index n] [-invoke] [-double]
          set      -name <text> | -id <id> <value> [-enter] [-keys]
          text     -name <text> | -id <id> | -type document [-limit n]
          wait     -name <text> | -id <id> [-type t] [-timeout ms] [-gone]

          ui lists visible interactive controls as {n,name,type,id,c:[x,y]}; c is the center in raw
          screen pixels (use with: click x y -abs). -raw adds non-interactive elements.
          -name matches exactly first, then as a case-insensitive substring. If several controls match,
          the call fails and lists candidates: refine with -type / -id, or pick the nth with -index.
          click -name uses a real mouse click; -invoke uses the control's UIA pattern instead (works for
          controls the mouse cannot reach, but may block if it opens a modal dialog).
          set uses ValuePattern when available, otherwise focuses, selects all and types (-keys forces that).
          UIA target window: -window/-process/-hwnd, else the last shot's window, else the foreground window.
          Custom-drawn UIs (games, ImGui/Vulkan, some Electron apps) expose no controls: use shot + coordinates.

        COORDINATES
          x/y are pixels in the image of the latest "shot" of the same -session. Its origin and scale are
          remembered, so downscaling and window position are handled for you. Use -abs for raw screen pixels.

        OPTIONS
          -shot            after an action, wait -wait ms and capture again (same target as the last shot;
                           add -screen to capture the whole screen instead)
          -wait <ms>       delay before that capture (default 400)
          -out <path>      output image path (.png / .jpg). Default: %TEMP%\computerUse_<timestamp>.png
          -maxside <n>     downscale so the longer side is <= n (default 1568, 0 = off)
          -session <id>    keep coordinate state separate per agent (default "default")
          -timeout <ms>    wait timeout (default 5000)
          -duration <ms>   drag duration (default 300)
          -abs             coordinates are raw screen pixels
          -h, -help        show this help
          --               everything after it is literal text (e.g. type -- "-x")

        KEYS (for "key"): ctrl shift alt win enter tab esc space backspace delete insert home end
          pageup pagedown up down left right f1..f24, or a single character. Join with + (use "plus" / "minus").

        EXIT CODES: 0 ok | 1 bad arguments | 2 window or element not found | 3 failed

        TYPICAL LOOP
          computerUse shot -window "Notepad"      -> {"ok":true,"path":"...","width":..,"height":..}
          computerUse click -name "Save" -shot    -> click the control by name, then a fresh screenshot
          computerUse type "hello" -enter -shot
        """;
}
