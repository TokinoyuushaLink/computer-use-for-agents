using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ComputerUse;

internal sealed record UiTop(long Hwnd, string Title, AutomationElement Root);

internal sealed record UiItem(
    AutomationElement Element,
    string Name,
    string Type,
    string Id,
    Rectangle Bounds,
    bool Enabled,
    string? Value,
    string? Toggle,
    bool? Selected)
{
    public bool HasBounds => Bounds.Width > 0 && Bounds.Height > 0;
    public Point Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
}

/// <summary>UI Automation: find controls by name / id / type and act on them.</summary>
internal static class Uia
{
    private static readonly Dictionary<string, ControlType> InteractiveTypes = new()
    {
        ["Button"] = ControlType.Button,
        ["CheckBox"] = ControlType.CheckBox,
        ["ComboBox"] = ControlType.ComboBox,
        ["Edit"] = ControlType.Edit,
        ["Hyperlink"] = ControlType.Hyperlink,
        ["ListItem"] = ControlType.ListItem,
        ["MenuItem"] = ControlType.MenuItem,
        ["RadioButton"] = ControlType.RadioButton,
        ["TabItem"] = ControlType.TabItem,
        ["TreeItem"] = ControlType.TreeItem,
        ["Slider"] = ControlType.Slider,
        ["Spinner"] = ControlType.Spinner,
        ["SplitButton"] = ControlType.SplitButton,
        ["DataItem"] = ControlType.DataItem,
        ["Document"] = ControlType.Document
    };

    /// <summary>Types worth listing even when they have neither name nor id.</summary>
    private static readonly HashSet<string> UnnamedOk = ["Edit", "Document", "ComboBox"];

    /// <summary>Everything we read is prefetched in one cross-process call instead of one call per property.</summary>
    private static readonly AutomationProperty[] CachedProperties =
    [
        AutomationElement.NameProperty,
        AutomationElement.ControlTypeProperty,
        AutomationElement.AutomationIdProperty,
        AutomationElement.BoundingRectangleProperty,
        AutomationElement.IsEnabledProperty,
        ValuePattern.ValueProperty,
        TogglePattern.ToggleStateProperty,
        SelectionItemPattern.IsSelectedProperty
    ];

    // ---- target window -------------------------------------------------------------------------

    /// <summary>-window/-process/-hwnd, else the window of the last shot, else the foreground window.</summary>
    public static UiTop ResolveTop(Options o)
    {
        long hwnd;
        string? title;
        if (o.HasWindowSelector)
        {
            var window = WindowFinder.Resolve(o);
            hwnd = window.Hwnd.ToInt64();
            title = window.Title;
        }
        else if (StateStore.Load(o) is { Mode: TargetMode.Window } last && Native.IsWindow(new IntPtr(last.Hwnd)))
        {
            hwnd = last.Hwnd;
            title = last.Title;
        }
        else
        {
            hwnd = Native.GetForegroundWindow().ToInt64();
            title = null;
        }

        if (hwnd == 0)
            throw AppError.NotFound("no target window (use -window, -process or -hwnd)");

        var handle = new IntPtr(hwnd);
        if (Native.IsIconic(handle))
        {
            Native.Focus(handle);
            Thread.Sleep(300);
        }

        try
        {
            var root = AutomationElement.FromHandle(handle);
            return new UiTop(hwnd, title ?? root.Current.Name, root);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw AppError.Failed($"UI Automation unavailable: {e.Message}");
        }
    }

    // ---- querying ------------------------------------------------------------------------------

    /// <summary>Controls matching -id/-type/-name. Interactive controls first; if none match, everything visible.</summary>
    public static List<UiItem> Find(UiTop top, Options o)
    {
        var matches = Filter(Query(top.Root, o.Raw), o);
        return matches.Count == 0 && !o.Raw ? Filter(Query(top.Root, raw: true), o) : matches;
    }

    private static List<UiItem> Query(AutomationElement root, bool raw)
    {
        Condition condition = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
        if (!raw)
        {
            var anyInteractiveType = new OrCondition(InteractiveTypes.Values
                .Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t))
                .ToArray());
            condition = new AndCondition(condition, anyInteractiveType);
        }

        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.Full };
        foreach (var property in CachedProperties)
            cache.Add(property);

        AutomationElementCollection found;
        try
        {
            using (cache.Activate())
                found = root.FindAll(TreeScope.Descendants, condition);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw AppError.Failed($"UIA query failed: {e.Message}");
        }

        var items = new List<UiItem>();
        foreach (AutomationElement element in found)
        {
            var cached = element.Cached;
            var type = cached.ControlType.ProgrammaticName.Replace("ControlType.", "");
            var name = cached.Name ?? "";
            var id = cached.AutomationId ?? "";
            if (name.Length == 0 && id.Length == 0 && !UnnamedOk.Contains(type))
                continue;

            var rect = cached.BoundingRectangle;
            var bounds = rect.IsEmpty || double.IsInfinity(rect.X)
                ? Rectangle.Empty
                : new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);

            items.Add(new UiItem(element, name, type, id, bounds, cached.IsEnabled,
                CachedValue(element), CachedToggle(element), CachedSelected(element)));
        }
        return items;
    }

    // Unsupported patterns come back as a sentinel object, never as the expected type.
    private static string? CachedValue(AutomationElement e) =>
        e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;

    private static string? CachedToggle(AutomationElement e) =>
        e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) switch
        {
            ToggleState state => state.ToString().ToLowerInvariant(),
            int state => ((ToggleState)state).ToString().ToLowerInvariant(),
            _ => null
        };

    private static bool? CachedSelected(AutomationElement e) =>
        e.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is bool selected ? selected : null;

    private static List<UiItem> Filter(List<UiItem> items, Options o)
    {
        IEnumerable<UiItem> result = items;

        if (o.Id is not null)
            result = result.Where(x => x.Id == o.Id);

        if (o.Type is not null)
        {
            var types = o.Type.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            result = result.Where(x => types.Contains(x.Type, StringComparer.OrdinalIgnoreCase));
        }

        if (o.Name is not null)
        {
            var candidates = result.ToList();
            var exact = candidates.Where(x => x.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            result = exact.Count > 0
                ? exact
                : candidates.Where(x => x.Name.Contains(o.Name, StringComparison.OrdinalIgnoreCase));
        }
        return result.ToList();
    }

    /// <summary>The single match, the -index'th match, or an error listing candidates.</summary>
    public static UiItem Pick(List<UiItem> items, Options o)
    {
        if (items.Count == 0)
            throw AppError.NotFound($"no UI element matches {SelectorText(o)}");

        if (o.Index > 0)
        {
            if (o.Index > items.Count)
                throw AppError.BadArgs($"-index {o.Index} is out of range (1..{items.Count}) for {SelectorText(o)}");
            return items[o.Index - 1];
        }

        if (items.Count > 1)
        {
            var candidates = string.Join("; ", items.Take(8).Select(x => $"{x.Type}:'{x.Name}'" + (x.Id.Length > 0 ? $"#{x.Id}" : "")));
            throw AppError.BadArgs(
                $"{items.Count} elements match {SelectorText(o)}; refine with -type/-id or pick one with -index n. Candidates: {candidates}");
        }
        return items[0];
    }

    public static string SelectorText(Options o) => $"(name='{o.Name}' id='{o.Id}' type='{o.Type}')";

    // ---- output --------------------------------------------------------------------------------

    public static Result ToJson(UiItem item, int n)
    {
        var json = Result.Of(
            ("n", n),
            ("name", item.Name),
            ("type", item.Type),
            ("id", item.Id.Length > 0 ? item.Id : null),
            ("c", new[] { item.Center.X, item.Center.Y }));

        if (!item.Enabled)
            json["disabled"] = true;
        if (!string.IsNullOrEmpty(item.Value))
            json["value"] = item.Value.Length > 80 ? item.Value[..80] + "..." : item.Value;
        if (item.Toggle is not null)
            json["toggle"] = item.Toggle;
        if (item.Selected == true)
            json["selected"] = true;
        return json;
    }

    public static Result Summary(UiItem item) =>
        Result.Of(("name", item.Name), ("type", item.Type), ("id", item.Id.Length > 0 ? item.Id : null));

    // ---- acting --------------------------------------------------------------------------------

    /// <summary>Use the control's own pattern instead of the mouse. Returns which pattern was used.</summary>
    public static string Invoke(UiItem item)
    {
        var element = item.Element;
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            ((InvokePattern)pattern).Invoke();
            return "invoke";
        }
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
        {
            ((TogglePattern)pattern).Toggle();
            return "toggle";
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
        {
            ((SelectionItemPattern)pattern).Select();
            return "select";
        }
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
        {
            ((ExpandCollapsePattern)pattern).Expand();
            return "expand";
        }
        throw AppError.Failed("element supports no invoke-style pattern; omit -invoke for a mouse click");
    }

    /// <summary>Give the control keyboard focus, falling back to a click on its center.</summary>
    public static void FocusItem(UiTop top, UiItem item)
    {
        Native.Focus(new IntPtr(top.Hwnd));
        Thread.Sleep(100);
        try
        {
            item.Element.SetFocus();
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            Native.MoveCursor(item.Center);
            Thread.Sleep(40);
            Native.Click(MouseButton.Left);
        }
        Thread.Sleep(80);
    }

    /// <summary>Set the value through ValuePattern. False if the control does not support it or is read-only.</summary>
    public static bool TrySetValue(UiItem item, string text)
    {
        if (!item.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly)
            return false;

        ((ValuePattern)pattern).SetValue(text);
        return true;
    }

    /// <summary>Full text of an editor/document. Returns up to limit+1 characters so the caller can detect truncation.</summary>
    public static (string Text, string Via) ReadText(UiItem item, int limit)
    {
        if (item.Element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            return (((TextPattern)pattern).DocumentRange.GetText(limit + 1), "text");
        if (item.Value is not null)
            return (item.Value, "value");
        return (item.Name, "name");
    }
}
