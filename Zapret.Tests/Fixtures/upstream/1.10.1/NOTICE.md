# Upstream test fixtures — Flowseal Zapret 1.10.1

The `.bat` files in this directory are **unmodified copies** taken from
[`Flowseal/zapret-discord-youtube`](https://github.com/Flowseal/zapret-discord-youtube) at tag `1.10.1`.

They are checked in for one reason: the strategy parser and the compatibility layer must be tested
against what upstream actually ships, not against something hand-written that happens to agree with
the implementation. Keeping them here also means the whole test suite runs offline, with no engine
binary, no kernel driver, and no administrator rights.

Nothing executable is included — no `winws.exe`, no `WinDivert` driver, no `.bin` payloads. Tests
that need those files create empty placeholders at runtime, because the manager only ever checks
that they exist.

These files are redistributed under upstream's MIT licence, reproduced verbatim in
[`LICENSE.txt`](LICENSE.txt) in this directory.

Do not edit these files. When adding a fixture for a newer engine version, create a new directory
(`Fixtures/upstream/<version>/`) and copy the release contents in unchanged.
