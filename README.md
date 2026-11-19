# Comet MUD Client

A cross-platform MUD client for Windows, macOS and Linux, written in C# on .NET 10 with
Avalonia.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
```

```bash
dotnet test
```

## Layout

- `src/Comet.Core` - telnet, ANSI parsing, scrollback, sessions and the script engine. No UI
  dependencies.
- `tests/Comet.Core.Tests` - unit and loopback integration tests for the core.
