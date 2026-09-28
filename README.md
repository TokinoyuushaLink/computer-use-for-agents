# computer-use-for-agents

**A Windows desktop computer-use CLI for AI agents.** Use it as a local CUA when an agent has no native computer-use tool, cannot reach the Windows desktop, or needs a command-line interface for screenshots, mouse and keyboard input, and UI Automation (UIA).

`computerUse.exe` is a small C# desktop automation tool designed for coding agents and other CLI-based agents. It turns common Windows interactions into commands and returns machine-readable JSON, so an agent can inspect a result, choose its next action, and continue the task.

## Features

- Capture the screen, all displays, a window, or a screen region.
- Find and focus top-level windows by title, process name, or HWND.
- Click, double-click, move, drag, scroll, type text, and press key chords.
- Inspect standard Windows controls through Microsoft UI Automation (UIA).
- Find controls by name, AutomationId, and control type; click them, set values, read text, and wait for them to appear or disappear.
- Use coordinates from a screenshot, including when the image was scaled down or the target window was offset on the desktop.
- Keep screenshot-coordinate state separate between agents with `-session`.
- Return one-line JSON results and meaningful process exit codes for normal commands.

UI Automation works when the target application exposes accessible controls. Custom-drawn interfaces—including many ImGui apps, games, and canvas-based tools—may expose little or no UIA information. For those interfaces, capture a screenshot and interact with coordinates.

## Advantages

- **Works from an agent's CLI loop:** issue a command, parse its JSON, inspect a screenshot or control tree, then choose the next action.
- **Combines UIA and screenshots:** use named controls where available and image coordinates where the interface is custom drawn.
- **Preserves coordinate context:** later mouse commands can use coordinates from a prior screenshot; `-session` prevents concurrent agents from sharing that state.
- **Targets windows explicitly:** select a window by title, process, or handle instead of relying only on whichever window happens to be in front.

This is a local Windows desktop-control utility. It does not provide browser DOM access or make inaccessible UIA controls accessible.

## Requirements

- Windows x64
- .NET 8 Desktop Runtime x64 to run the framework-dependent executable
- .NET 8 SDK to build from source

The runtime is not bundled in the published executable. No PowerShell installation or `.ps1` file is needed.

## Build

The repository uses a flat layout: the `.cs` files, `computerUse.csproj`, and `app.manifest` are in the repository root. From that directory, run:

```powershell
dotnet publish .\computerUse.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

The single-file executable is written to `publish\computerUse.exe`.

## Agent workflow

1. List windows and choose a target:

   ```powershell
   .\computerUse.exe windows
   .\computerUse.exe windows -process notepad
   ```

2. Inspect its UIA controls or capture a screenshot:

   ```powershell
   .\computerUse.exe ui -process notepad -limit 30
   .\computerUse.exe shot -process notepad -session agent1
   ```

3. Use a control selector when UIA exposes the control, or use coordinates from the screenshot:

   ```powershell
   .\computerUse.exe click -name "Save" -process notepad
   .\computerUse.exe click 320 180 -session agent1 -shot
   ```

4. Read the JSON result, inspect any returned screenshot, and capture again after a layout change before reusing coordinates.

Each screenshot command returns a JSON object containing the image path, dimensions, scale, and target information. An agent can read that image from the returned path before choosing coordinates.

## Commands

| Command   | Purpose                                                      |
| --------- | ------------------------------------------------------------ |
| `shot`    | Capture the screen, all displays, a window, or a region      |
| `windows` | List or filter top-level windows                             |
| `focus`   | Focus a window                                               |
| `ui`      | List UIA controls                                            |
| `click`   | Click by screenshot coordinates, control name, or AutomationId |
| `set`     | Set a UIA editable value                                     |
| `text`    | Read text from a UIA element                                 |
| `wait`    | Wait for a UIA element to appear or disappear                |
| `move`    | Move the pointer                                             |
| `drag`    | Drag between two points                                      |
| `scroll`  | Scroll up or down                                            |
| `type`    | Type text, optionally followed by Enter                      |
| `key`     | Press a key or key chord such as `ctrl+s` or `alt+f4`        |

Run `computerUse.exe -help` for the full command syntax and options, including `-window`, `-process`, `-hwnd`, `-session`, `-shot`, `-abs`, `-timeout`, and `-duration`.

Normal commands print one JSON line with an `ok` field. Exit codes are `0` for success, `1` for invalid arguments, `2` when a window or UI element is not found, and `3` for other failures. `-help` prints text help.

## Safety and limitations

Mouse and keyboard commands act on the real desktop. Confirm the selected window and screenshot coordinates before sending input. Coordinates can become stale after a window moves, resizes, or changes layout; capture a fresh screenshot in the same session first.

UIA results depend on the target application's accessibility provider. `Invoke` is available for controls that support the relevant UIA pattern; some controls can open modal dialogs. Use screenshot-based interaction when UIA does not expose the required control.
