# Comet MUD Client

A cross-platform MUD client for Windows, macOS and Linux, written in C# on .NET 10 with
Avalonia.

## Features

- Tabbed sessions, each with its own scrollback, command history and script.
- ANSI 16-color, 256-color and 24-bit color output with bold, italic, underline, inverse and
  strikethrough. Font, colors and palette are configurable.
- Split scrollback: scrolling up keeps the live output visible in a pane below. Find, mouse
  selection, copy and clickable http/https links.
- Telnet with TLS, MCCP2 compression, NAWS, terminal type (MTTS), CHARSET, GMCP and GA/EOR
  prompts. The input is masked while the server asks for a password, and passwords are never
  written to the history, the log or disk.
- Scripting from the command line or the script editor: aliases, actions (triggers),
  substitutions, gags, highlights, variables, tickers, delays, key macros, speedwalk, `#if`,
  `#math` and `#loop`.
- Session logging, saved connections with optional automatic reconnect, and tab completion
  from words seen in the output.

## Run

Requires the .NET 10 SDK.

```bash
dotnet run --project src/Comet.App
```

To build a self-contained app for one platform (`win-x64`, `osx-arm64`, `linux-x64`, ...):

```bash
dotnet publish src/Comet.App -c Release -r win-x64 --self-contained
```

## Build and test

```bash
dotnet build
```

```bash
dotnet test
```

The UI tests run the real windows without a display and save screenshots to
`artifacts/screenshots`.

## Scripting in one minute

Commands start with `#` and may be shortened (`#al` for `#alias`). Arguments that contain
spaces go in braces. `;` separates commands.

```
#alias {k} {kill %1;loot corpse}
#action {^%1 tells you '%2'} {reply %1 I am away}
#highlight {light red, underline} {orc}
#gag {^The wind howls}
#variable {target} {orc}
#ticker {save} {save} {300}
#macro {F5} {cast heal}
#5 {north}
3n2e
```

Type `#help` in the client, or open Help > Command Reference, for every command. Patterns
use `^` and `$` as anchors, `%1` to `%99` to capture text, `%d` and `%w` for digits and
words, and `{regex}` for a regular expression.

## Where files are kept

| What | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Settings and scripts | `%APPDATA%\Comet` | `~/Library/Application Support/Comet` | `~/.config/Comet` |
| Logs | `Documents\Comet\Logs` | `~/Documents/Comet/Logs` | `~/Documents/Comet/Logs` |

Help > About shows the exact folders on your machine.

Each saved connection has its own script file, written automatically when you change its
aliases, actions or other items.

## Layout

- `src/Comet.Core` - telnet, ANSI parsing, scrollback, sessions and the script engine. No UI
  dependencies.
- `src/Comet.App` - the Avalonia desktop application.
- `tests/Comet.Core.Tests` - unit tests and loopback telnet tests for the core.
- `tests/Comet.App.Tests` - headless tests that drive the real windows.
