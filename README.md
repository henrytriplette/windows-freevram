# freevram

Find out what's holding your GPU's VRAM on Windows, kill it, and flush what's left.
Ships as a tray app (`tray/`) and a PowerShell script (`freevram.ps1`) that do the same thing.

Windows has no "free VRAM" call. Memory is owned by processes; the only real
levers are ending the process or resetting the display driver. This script
makes both a one-liner. It reads the WDDM `GPU Process Memory` performance
counters, so it works for NVIDIA/AMD/Intel and does not need admin.

## Tray app

```powershell
cd tray
dotnet publish -c Release -o ../dist     # needs .NET 10 SDK to build; exe is self-contained (~47 MB), no runtime needed to run
..\dist\FreeVram.exe
```

- Icon is a VRAM meter: green < 70%, amber < 90%, red above. Hover for GB used.
- Right-click: top 12 VRAM consumers. Click one to kill it (with confirmation).
  System processes are greyed out; shell processes are amber and warn you.
- Double-click or use the menu to restart the graphics driver; a balloon shows the before/after.
- Balloon warning once when usage crosses 90%.
- "Start with Windows" writes `HKCU\...\Run\FreeVram`; unchecking removes it.
- Single instance. New tray icons land in the overflow (^) area until you drag them out.

## Script

```powershell
.\freevram.ps1                       # ranked list of VRAM consumers
.\freevram.ps1 -Interactive          # numbered list, pick what to kill
.\freevram.ps1 -Kill opera,ms-teams  # kill by name or PID
.\freevram.ps1 -RestartDriver        # Win+Ctrl+Shift+B, reports before/after
.\freevram.ps1 -Watch                # live view, refresh every 2s
.\freevram.ps1 -MinMB 50 -Top 10     # filter the list
```

If scripts are blocked: `powershell -ExecutionPolicy Bypass -File .\freevram.ps1`.

## Safety

- System processes that would take the desktop down (`dwm`, `csrss`, `winlogon`, ...)
  are shown greyed out and never killed.
- Shell processes (`explorer`, `StartMenuExperienceHost`, ...) are shown in yellow
  and need `-Force`.
- `-Interactive` asks for confirmation before killing; `-Force` skips it.

## Notes

- `nvidia-smi` shows `N/A` per process on WDDM; the perf counters are the
  supported way to get per-process VRAM without a native D3DKMT client.
- `-RestartDriver` synthesises the built-in hotkey. There is no public API for it.
- Dedicated = on-card VRAM. Shared = system RAM the GPU is mapped into.
# windows-freevram
