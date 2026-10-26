# matrix-rain

The green "digital rain" from *The Matrix* as a Windows screensaver. It covers
every monitor, needs nothing installed, and builds with the C# compiler that
already ships with Windows.

Two versions:

| File | What it is |
|---|---|
| `Matrix.cs` | The original. GDI+ on the CPU, one window per monitor. About 10 KB built. |
| `MatrixGlow.cs` | GPU version (OpenGL 3.3) with a bloom glow on the bright characters. One window across all monitors, so the rain lines up between screens. |

Both use mirrored half-width katakana and digits, near-white leading characters,
teal-green trails that fade to black, and characters that flicker as they fall.

## Build

A `.scr` is just an `.exe` with a different extension. From Command Prompt in
this folder:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /out:Matrix.scr /r:System.Windows.Forms.dll /r:System.Drawing.dll Matrix.cs
```

For the glow version, swap `Matrix` for `MatrixGlow` in both places.

From WSL, use `/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe`
with the same arguments.

## Install

1. Right-click the `.scr` file and choose **Test** to preview it. Move the
   mouse or press a key to exit.
2. Right-click it again and choose **Install**. Windows opens the screensaver
   settings with it selected. Set the wait time there.

Windows uses the file where it sits, so rebuilding updates the installed
screensaver.

## Tweaking

Settings are constants near the top of each class:

- `SpawnChance`, `MinGap`: how dense the rain is
- `CellW`, `CellH` and the font size: character size
- `rnd.Next(10, 50)`: trail length range
- `Interval = 33`: frame rate (about 30 fps)
- `MatrixGlow.cs` only: `GlowStrength` and the number of blur passes

## Checking the GPU version

```
MatrixGlow.scr /selftest
```

This renders a few frames in a tiny off-screen window and exits with code 0 if
everything worked. If not, the error is written to
`%TEMP%\matrixglow-selftest.txt`.
