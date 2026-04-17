# Desktop Whiteboard

A lightweight Windows desktop annotation tool built with WPF (.NET 10). Draw, highlight, and annotate directly on top of your wallpaper using ink strokes, text, and images — all without interrupting your workflow.

---

## How It Works

Desktop Whiteboard runs as a transparent, always-on-top overlay that normally sits invisible in the background. When you activate edit mode, it becomes a full-screen canvas layered over your wallpaper. When you exit, it **bakes your drawings directly into a composite wallpaper image** and sets that as your desktop background — so your annotations are always visible, even when no apps are open.

```
[Your original wallpaper] + [ink strokes + text] → composite wallpaper PNG → set as desktop
```

The app uses a single global hotkey (`Ctrl + Alt + W`) to toggle between passive (invisible) and active (drawing) states.

---

## Features

### Drawing Tools
| Tool | Description |
|------|-------------|
| **Pen** | Freehand ink drawing with pressure-independent strokes |
| **Highlighter** | Semi-transparent wide strokes (alpha 120, 4.5× scaled width) |
| **Eraser** | Erase entire strokes by touch (`EraseByStroke` mode) |
| **Text** | Click-to-place draggable text boxes with full formatting |

### Text Formatting
- Font size slider (10–72 pt)
- Bold, Italic, Underline toggle buttons
- Drag text boxes to reposition them anywhere on the canvas
- Formatting syncs when you click an existing text box

### Colors
- 6 preset ink colors: White, Black, Red, Blue, Green, Yellow
- Color selection applies to both pen and highlighter (highlighter forces 50% transparency)
- Active color button highlighted with white border

### Canvas
- **Pen thickness** slider (1–10, integer steps)
- **Light / Dark** canvas theme (auto-detected from Windows system theme on first launch)
- **Clear** button wipes all strokes and text

### Wallpaper Integration
- Saves your original wallpaper path on first run (stored in `%LocalAppData%\DesktopWhiteboard\original_wallpaper.txt`)
- Composites drawings onto the original wallpaper and saves to `wallpaper.png`
- Uses Win32 `SystemParametersInfo` to apply the composite as the desktop wallpaper
- Restores the original wallpaper when entering edit mode to prevent double-rendering of strokes
- Wallpaper style forced to "Fill" (registry key `WallpaperStyle = 10`)

### Persistence
- Ink strokes saved as ISF (Ink Serialized Format) to `%LocalAppData%\DesktopWhiteboard\strokes.isf`
- Text boxes saved as JSON to `%LocalAppData%\DesktopWhiteboard\text.json` (position, size, formatting, color)
- Strokes and text are reloaded automatically on next launch

### Undo / Redo
- Full undo/redo stack for ink strokes (`Ctrl+Z` / `Ctrl+Y`)
- Snapshot-based: each stroke change pushes the previous collection to the undo stack

### Image Support
- Paste images from clipboard (`Ctrl+V`)
- Images placed in a resizable, draggable border container
- Bottom-right resize handle (thumb control)

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl + Alt + W` | Toggle edit mode on/off (global hotkey) |
| `Esc` | Exit edit mode |
| `Ctrl + Z` | Undo last stroke |
| `Ctrl + Y` | Redo |
| `E` | Switch to Eraser tool |
| `I` | Switch to Pen tool |
| `Ctrl + V` | Paste image from clipboard |
| `Ctrl + Shift + C` | Clear all strokes |
| `F1` | Show keyboard shortcuts overlay |
| `Enter` | Finalize active text box |
| `Delete` | Remove active text box |

---

## Architecture

### Project Structure

```
DesktopWhiteboard/
├── App.xaml              # Application entry point
├── App.xaml.cs           # Application class (minimal)
├── MainWindow.xaml       # UI layout: InkCanvas, TextLayer, ImageLayer, OverlayMenu
├── MainWindow.xaml.cs    # All logic: drawing, tools, persistence, wallpaper
├── AssemblyInfo.cs       # Assembly metadata
└── DesktopWhiteboard.csproj  # .NET 10 WPF project file
```

### Layer Stack (Z-order, bottom to top)
1. **InkCanvas (`Whiteboard`)** — ink stroke surface
2. **Canvas (`ImageLayer`)** — pasted images, hit-test disabled when not in edit mode
3. **Canvas (`TextLayer`)** — text boxes, hit-test disabled when not in edit mode
4. **Border (`OverlayMenu`)** — top-right toolbar, visible only in edit mode
5. **Border (`HelpOverlay`)** — F1 shortcuts panel

### Key Win32 Integrations
- `GetWindowLong` / `SetWindowLong` — toggle `WS_EX_TRANSPARENT` for click-through passthrough
- `RegisterHotKey` / `UnregisterHotKey` — global `Ctrl+Alt+W` hotkey via `WM_HOTKEY` message
- `SystemParametersInfo(SPI_SETDESKWALLPAPER)` — programmatically set desktop wallpaper
- Registry read (`Control Panel\Desktop`) — retrieve current wallpaper path
- Registry write (`WallpaperStyle`, `TileWallpaper`) — force Fill scaling

### Singleton Guard
A named `Mutex` (`"DesktopWhiteboardSingleton"`) prevents multiple instances from running simultaneously.

---

## Requirements

- **OS**: Windows 10 or Windows 11
- **Runtime**: .NET 10 (Windows)
- **Framework**: WPF (Windows Presentation Foundation)

---

## Building

```bash
# Build (Release)
dotnet build -c Release

# Run
dotnet run

# Publish self-contained x64 executable
dotnet publish -c Release -r win-x64 --self-contained
```

The compiled executable is output to `bin\Release\net10.0-windows\win-x64\`.

---

## Data Files

All persistent data is stored under `%LocalAppData%\DesktopWhiteboard\`:

| File | Contents |
|------|----------|
| `strokes.isf` | Ink strokes in Ink Serialized Format |
| `text.json` | Text box positions, content, and formatting |
| `wallpaper.png` | Composite wallpaper (original + drawings) |
| `original_wallpaper.txt` | Path to the user's original wallpaper (written once) |

---

## Notes

- The window is `Topmost="True"` and `WindowStyle="None"` with `AllowsTransparency="True"`, making it a fully transparent overlay.
- Touch input is explicitly blocked on the InkCanvas to avoid interference with stylus/mouse drawing.
- The app hides itself from the taskbar (`ShowInTaskbar="False"`) and system tray — it is accessed entirely via the global hotkey.
- Strokes are auto-saved on every change (via `StrokesChanged` event) and on window close.
