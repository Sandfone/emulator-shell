# scrcpy-server 5.0.1

`scrcpy-server` is the device-side part of [scrcpy](https://github.com/Genymobile/scrcpy) 5.0.1
(Apache License 2.0, see `LICENSE`), unchanged. Emulator Shell pushes it to a physical device and runs it
with `app_process` to stream the screen and sound and to inject input (`Sources/EmulatorShell/Scrcpy.swift`).

The client must pass the server's exact version, so `ScrcpyStream.serverVersion` has to change together
with this file. To update it, copy the new `scrcpy-server` from a scrcpy release (or
`$(brew --prefix)/share/scrcpy/scrcpy-server`) and check the protocol changes in that release's
`doc/develop.md`.

SHA-256 of this copy: `764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536`.
