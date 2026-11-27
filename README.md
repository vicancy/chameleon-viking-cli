# Chameleon Viking CLI

A small .NET 10 USB client for reading Viking 125 kHz tags and writing their IDs to blank T5577/T55xx tags with ChameleonUltra. Writes are followed by two matching readbacks before the tool reports success.

The RFID decoding and programming capabilities come from ChameleonUltra firmware. This project is an independent client built around its existing commands, with card-file storage and a focused verification workflow.

[中文使用说明](使用说明.txt)

## Requirements

- .NET 10 SDK to build or run from source; .NET 10 Runtime to use the framework-dependent executable.
- ChameleonUltra connected by a USB data cable, with firmware supporting Viking scan and T55xx write commands (`3004` and `3005`). ChameleonLite is not supported.
- A readable Viking source tag and a blank, writable T5577/T55xx target. Ordinary 13.56 MHz MIFARE/UID/CUID cards are not suitable targets.

Windows hardware testing used firmware `v2.3.0-EV`. Other firmware versions are checked for command support at connection time. Linux and macOS can use explicit serial ports, but hardware read/write behavior on those platforms has not been verified. CLI messages are currently in Simplified Chinese; command names and JSON keys are in English.

## Read, write, verify

Run these commands from the repository directory:

```sh
dotnet run --project VikingTool.csproj -- ports
dotnet run --project VikingTool.csproj -- info
dotnet run --project VikingTool.csproj -- read --out my-card.viking.json
```

Remove the source tag and place **only the blank T5577** on the reader. The following command overwrites that target:

```sh
dotnet run --project VikingTool.csproj -- write --from my-card.viking.json --blank-t5577
```

To check the target again without writing:

```sh
dotnet run --project VikingTool.csproj -- verify --from my-card.viking.json
```

Windows automatically selects a single detected ChameleonUltra. If several devices are attached, or on Linux/macOS, add `--port` to `info`, `read`, `write`, or `verify`:

```sh
dotnet run --project VikingTool.csproj -- info --port COM3
dotnet run --project VikingTool.csproj -- info --port /dev/ttyACM0
```

Use the actual port returned by `ports`; macOS typically uses `/dev/cu.usbmodem...`. Close other software connected to the same reader. Keep the tag close to the antenna and move metal keys and other tags away.

## Commands and files

| Command | Purpose |
| --- | --- |
| `ports` | List serial ports without opening them |
| `info` | Check the connected device, firmware and Viking capabilities |
| `read [--out FILE]` | Obtain two matching reads and save a card record |
| `write --from FILE --blank-t5577` | Program a blank target and independently verify its ID twice |
| `verify --from FILE` | Compare two reads with a saved ID; never writes |
| `help` | Show all options |

`write` and `verify` also accept `--id 12345678` instead of `--from`; IDs contain exactly eight hexadecimal characters. The example ID is fictional.

Without `--out`, a successful read updates `last-read.viking.json` next to the executable. When using `dotnet run`, that location is the build output directory; use an explicit `--out` for a convenient path. Failed or unstable reads do not replace an existing card file. Card files contain credential identifiers: keep them local. `*.viking.json` is ignored by Git.

Add `--json` for one result object on stdout; progress and warnings go to stderr. Exit codes are `0` for success, `1` for device/read/write/file errors, and `2` for usage or port-selection errors.

## Write behavior

- The tool checks firmware capabilities, enters reader mode when needed, and attempts to restore the previous mode afterward.
- If a preliminary scan finds existing Viking data, the write is refused. This is a best-effort source-tag guard, not proof that an undetected tag is blank or a T5577. Physically replacing the source is still necessary.
- A firmware acknowledgement alone is not treated as a successful write. The programmed ID must be read back twice and match the source.
- A write command is sent at most once. After a failure or timeout, the target may already have changed; run `verify` before attempting anything else.
- Matching readbacks verify the stored Viking ID. Acceptance by the actual access-control reader must be tested separately.

The upstream write operation enables rewritable T5577 password protection using the public default password `20206666` (hex). Another tool may need that password to reprogram the tag. The operation does not set irreversible lock bits. This CLI intentionally writes blank targets rather than overwriting already detectable Viking tags.

## Build and test

```sh
dotnet build -c Release
dotnet run --project tests/VikingTool.Tests.csproj -c Release
pwsh -File tests/cli-smoke.ps1
```

The tests use simulated device responses and fictional card records; no reader or real credential is needed. CI runs the build and tests on Windows, Linux and macOS. These checks do not constitute hardware validation on all three platforms.

To publish a Windows x64 single-file executable:

```powershell
.\build.ps1
.\viking.exe info
```

The script writes `artifacts/win-x64/viking.exe` and copies it to the repository root. The executable requires the .NET 10 Runtime. Dependencies are restored from Microsoft's public `dotnet-public` NuGet feed configured in `NuGet.Config`.

## License and upstream

Licensed under [GPL-3.0-only](LICENSE). See [NOTICE](NOTICE) for the ChameleonUltra sources used as protocol and implementation references. Microsoft runtime and package dependencies retain their respective licenses.
