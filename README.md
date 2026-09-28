# computer-use-for-agents

A PowerShell desktop-control function for taking screenshots and interacting with Windows applications through mouse, keyboard, and UI Automation (UIA).

## Features

- Capture the full screen, all displays, a window, or a screen region.
- List and focus windows by title or process name.
- Click, double-click, move, drag, scroll, type text, and press key chords.
- Read UI Automation controls and text from applications that expose them.
- Click controls by name or AutomationId, and set editable values.
- Map screenshot coordinates back to screen coordinates, including when screenshots are downscaled.

Custom-drawn interfaces such as many ImGui applications may not expose useful UIA controls. Use screenshots and coordinates for those interfaces.

## Usage

Load the function into your PowerShell session, then call it as `computerUse`.

```powershell
computerUse -Help
```

### Screenshots and windows

```powershell
# Capture the primary display
computerUse shot -screen

# Capture all displays
computerUse shot -all

# Capture a window or process
computerUse shot -window "Notepad"
computerUse shot -process "notepad"

# List windows and focus one
computerUse windows
computerUse focus -window "Notepad"
```

Screenshots return a JSON result containing the image path, dimensions, scale, and target window. Coordinates from the latest screenshot are remembered for later mouse commands.

### Mouse and keyboard

```powershell
# Click at coordinates in the latest screenshot
computerUse click 320 180

# Double-click, right-click, or use raw screen coordinates
computerUse click 320 180 -double
computerUse click 320 180 -button right
computerUse click 786 220 -abs

# Drag and scroll
computerUse drag 300 200 500 400
computerUse scroll down 5 700 600

# Type text and press keys
computerUse type "hello"
computerUse type "search terms" -enter
computerUse key ctrl+a
computerUse key alt+f4
```

Add `-shot` to capture the screen again after an action:

```powershell
computerUse click 320 180 -shot
```

### UI Automation

List controls exposed by the target window:

```powershell
computerUse ui
computerUse ui -window "Notepad"
computerUse ui -type Edit
computerUse ui -name "Save"
computerUse ui -raw
```

Each returned control includes its name, type, AutomationId when available, and center point. `-raw` includes non-interactive elements.

Click a control by name or AutomationId:

```powershell
computerUse click -name "Save"
computerUse click -id "SearchEditBox"
```

If several controls match, narrow the search with `-type` or `-id`, or select a match with `-index`:

```powershell
computerUse click -name "Open" -type Button -index 2
```

Use `-invoke` to invoke a supported UIA control pattern instead of sending a mouse click:

```powershell
computerUse click -name "Save" -invoke
```

Set an editable control's value:

```powershell
computerUse set -name "File name" "report.txt"
computerUse set -id "SearchEditBox" "query" -enter
```

Read text exposed by UIA:

```powershell
computerUse text -name "File name"
computerUse text -type document
```

### Target selection

Commands accept `-window` or `-process` to choose a target:

```powershell
computerUse ui -window "Notepad"
computerUse click -name "Save" -process "notepad"
```

Without an explicit target, the function uses the last screenshot's window when available, otherwise the foreground window.

## Coordinates

By default, mouse coordinates refer to the most recent screenshot. The function remembers the screenshot's origin and scale, so coordinates continue to work when the screenshot is downscaled or the target window is offset on the desktop.

Use `-abs` when passing raw screen coordinates:

```powershell
computerUse click 786 220 -abs
```

## Limitations

- UIA can only inspect controls that an application exposes through its accessibility interface.
- Games, canvas-based interfaces, and many custom-drawn UIs may expose few or no useful UIA controls. Use screenshot-based coordinates for those.
- UIA `Invoke` operations depend on the control supporting the relevant pattern. Some operations can open modal dialogs.
- Coordinates can become stale after a window moves, resizes, or changes layout. Capture a fresh screenshot before choosing new coordinates.

## Command reference

```text
shot     Capture a screen, window, or region
windows  List matching windows
focus    Focus a window
ui       List UIA controls
text     Read text from a UIA element
click    Click by coordinates, name, or AutomationId
set      Set a UIA editable value
move     Move the pointer
drag     Drag between two points
scroll   Scroll up or down
type     Type text, optionally followed by Enter
key      Press one or more key chords
```
