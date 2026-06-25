<div align="center">

# ScrollVD

### Turn a single monitor into a large, scrollable desktop.

<img src="docs/hero.svg" alt="ScrollVD concept" width="100%">

![Windows](https://img.shields.io/badge/Windows-x64-0078D6?logo=windows&logoColor=white)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![Tray app](https://img.shields.io/badge/runs%20in-system%20tray-1d264c)

[**⬇ Download the latest release**](https://github.com/yarhoroh/ScrollVD/releases/latest)

</div>

---

## What it is

ScrollVD is a tiny **system-tray** app that gives you a desktop bigger than your screen.
It lays your windows out across a **3 × 3 grid of off-screen regions** and lets you pan / jump
between them — like having extra desktops, except it works by **moving the windows themselves**.
No real Windows virtual desktops are created, so nothing is left behind when you quit.

> Pick the layout that suits you in **Settings → Grid layout** — from a simple 2-cell
> setup (main + one extra, in any direction) up to 2×2, 3×2, 2×3 or the classic 3×3.
> You choose how many cells and where the **main** screen sits.

## Features

| | |
|---|---|
| 🧩 **Choose your layout** | Pick the grid in Settings — 2 cells (main + one extra, any side), 2×2, 3×2, 2×3 or the classic 3×3 — and where the main screen sits. |
| 🧭 **Grid mode** | Push the mouse to a screen edge to jump exactly one full screen in that direction. |
| 🗺️ **Minimap** | A small always-on-top overview of the whole canvas and where every window sits. |
| 🔍 **Live previews** | Hover a minimap cell to see a real thumbnail of what is on it — even off-screen cells. |
| 🖱️ **Right-click to place** | Right-click a cell to pick any window (including minimized) and send it there. |
| ✋ **Drag to throw** | Drag a window straight onto a minimap cell to move it into that region. |
| 🧲 **Bring it to you** | Activating a window that lives on another cell pulls it onto the current screen. |
| 🟦 **App icons in cells** | Each minimap cell shows the icons of the apps it contains (up to 6, then “+N”), so you can see what's where at a glance — no need to preview every cell. |
| 🔢 **Desktop number** | When you have more than one Windows virtual desktop, the current desktop number is shown faintly in the centre of the minimap. |

<div align="center">
<img src="docs/minimap.png" alt="ScrollVD minimap" width="360"><br>
<sub>The minimap — the lighter rectangle is your current view on the canvas.</sub>
</div>

## Install & run

1. Download **`ScrollVD.exe`** from the [latest release](https://github.com/yarhoroh/ScrollVD/releases/latest).
2. Run it. It is **self-contained** (the .NET runtime is bundled) — nothing to install. Requires **Windows x64**.
3. It starts in the **system tray** (icon near the clock). **Right-click** the icon for options and settings.

> [!NOTE]
> The build is **not code-signed**, so Windows SmartScreen will warn you:
> click **More info → Run anyway**. Some antivirus may flag the single-file bundle —
> add an exception if needed.

## How it works

There is just **one** coordinate canvas (your virtual screen). The "9 screens" are simply
**9 regions** of that canvas; neighbouring cells live *outside* the visible area of your monitor.
When you pan or jump, every window's X/Y is shifted together by one screen — windows slide in and
out of view. The engine only tracks the accumulated offset, and **Reset** sends every window back home.

Windows' own virtual desktops are only *read* (never created) so ScrollVD can keep a separate
offset per desktop and avoid moving windows that belong to a different one.

## Controls

| Action | How |
|---|---|
| Jump one screen | Move the cursor to a screen edge (grid mode) |
| Require Shift for edge jumps | Enable **Settings → Edge jump only while Shift is held** — then the edge only fires while `Shift` is down |
| Toggle the minimap | Double-tap the minimap hotkey, or use the tray menu |
| Move the minimap | `Ctrl` + drag |
| Resize the minimap | Drag its bottom-right corner |
| Pan via minimap | Click / drag inside the minimap |
| Send a window to a cell | Right-click a cell → pick a window, **or** drag a window onto the cell |
| Carry a window to the next cell | Drag a window to a screen edge — the view pans one cell while the window rides along under the cursor |
| Bring active window here | Tray menu → *Bring active window here* |
| Reset all windows | Tray menu / Settings → *Reset window positions* |

## Your windows are never lost

ScrollVD restores every window to its home position **automatically**:

- **On startup** — any window left off-screen by a previous session is brought back (across every virtual desktop, including minimized windows whose restore position drifted off-screen).
- **On exit** — all windows are returned to their positions across every virtual desktop.

You can also do it manually any time: tray menu → **Reset window positions** (current desktop) or
**Reset window positions (all desktops)**, or **Settings → Reset window positions**.

Right-click the **tray icon** (near the clock) for the main menu: enable/disable panning, toggle the
minimap, *Bring active window here*, *Reset window positions*, open **Settings**, and **Exit**.

## Build from source

```bash
dotnet build src/ScrollVD.csproj -c Release
```

Self-contained single-file (what the release ships):

```bash
dotnet publish src/ScrollVD.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

## Notes

ScrollVD is a from-scratch take on the "panning / scrollable desktop" idea (à la GimeSpace
Desktop Extender). It is a Windows-only WinForms app built on Win32 APIs (`SetWindowsHookEx`,
`EnumWindows`, `SetWindowPos`, `PrintWindow`, the virtual-desktop COM interface) and does not run
on macOS or Linux.
