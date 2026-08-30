# Controller Hub Native UI - Keyboard + Mouse / Controller

Windows native controller input + direct TCP sender.

## Input modes

When the EXE starts, it asks whether to use:
- CONTROLLER
- KEYBOARD + MOUSE

### Keyboard + Mouse defaults
- Left stick: W/A/S/D
- Normal stick magnitude: 0.8
- Shift held: 1.0
- Ctrl held: 0.5
- Right stick: mouse X/Y
- Mouse sensitivity: 1.0

Mouse movement is read from the Windows cursor position during the polling loop, so movement is not lost when the pointer is over child controls such as the stick display or log panels.

F8 still toggles mouse capture mode. Capture hides the cursor and recenters it each poll for continuous aiming.

## TCP
Default target: `192.168.6.20:12345`.

The receiver expects:
`XX,XX,XX,XX,XX,XX,XX\r\n`

## Build

Install Visual Studio 2022 with:
- .NET desktop development
- Windows 11 SDK

Then open `ControllerHubNative.csproj` and build/publish x64.

Command line:

    dotnet clean
    dotnet restore
    dotnet publish -c Release -r win-x64 --self-contained true
