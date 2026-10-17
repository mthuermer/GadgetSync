# GadgetSync

Companion mod for the [Derail Valley Multiplayer](https://github.com/AMacro/dv-multiplayer) mod that syncs
locomotive gadgets (the customization system) between host and clients. The Multiplayer mod itself (0.1.16)
does not sync gadgets, so without this mod every player only sees their own.

The mod's internal id is `GadgetMirror` (folder, DLL and entry in Unity Mod Manager).

## Features (0.7.0)

- **Host → clients:** all installed gadgets of every car, with position, state, wiring, soldering, mount points,
  labels and control positions. This also covers players who join later and loaded saves.
- **Operating:** clients can use gadgets like the host does: switches, dials, levers, the SwitchSetter (including its sound)
  and the ATS acknowledge button.
- **Building:** clients can install and remove gadgets, screw or tape mount points, solder, wire and label.
  The host carries it out and distributes the result.
- **Host far away:** everything keeps working when the host is far from the car and has not loaded the gadget models.
- **Version check:** a player with a different GadgetSync version sees the gadgets but cannot operate them,
  and a chat message says so.

## Requirements

- Derail Valley (Steam) with Unity Mod Manager 0.27.3 or newer
- [Multiplayer](https://github.com/AMacro/dv-multiplayer) 0.1.16
- **Every player** needs GadgetSync, in the same version

## Installation

Put `GadgetMirror.dll` and `Info.json` into `Derail Valley/Mods/GadgetMirror/`, or install a zip containing that
folder through Unity Mod Manager.

## Known limitations

- Loose items (maps, job papers, radio, remote controller) are not synced. The Multiplayer mod has its item sync
  switched off.
- Button click sounds are heard only by the player pressing the button (the SwitchSetter is the exception).
- Tested with two players (one host, one client).

## How it works

- The host sends a car's gadget state as a snapshot whenever it changes (immediately, at most every 0.1 s, plus a
  check every second). Clients rebuild only what changed.
- A client applies its own input locally and sends only the change, numbered. The host confirms with the number
  of the snapshot that contains it. Until then the client keeps its input, so nothing jumps back. Snapshots and
  inputs carry sequence numbers because the Multiplayer API delivers mod packets reliably but not always in order.
- Bandwidth: nothing while idle. With 10 gadgets, about 1–4 KB/s while someone is operating a control.

## Building

- .NET SDK (target `netstandard2.0`) and the Multiplayer mod installed (the project references its
  `MultiplayerAPI.dll`).
- The game paths `DVManaged` and `DVMods` in `GadgetMirror.csproj` point to the default Steam location. For
  another location, pass them on the command line:

  ```
  dotnet build -c Release -p:DVManaged="<game>\DerailValley_Data\Managed" -p:DVMods="<game>\Mods"
  ```

- Output: `bin/Release/GadgetMirror.dll`

Code comments are in German.

## License

Apache License 2.0, see [LICENSE](LICENSE).
