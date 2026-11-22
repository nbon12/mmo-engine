# simcore: the shared simulation core's committed content

This folder holds the files the shared simulation core
([specs/015-shared-simulation-core](../specs/015-shared-simulation-core/spec.md)) reads and checks: a
configuration example, the test world, the recorded streams with their expected hashes, and the golden
fixtures of the three versioned formats. Every file here is either hand-edited or written by one
`simcore` command, and each section below names which. The commands are in
[contracts/simcore-cli.md](../specs/015-shared-simulation-core/contracts/simcore-cli.md); run them from
the repository root.

## config/

`default.json` is the configuration example of
[contracts/config-rules.md](../specs/015-shared-simulation-core/contracts/config-rules.md), and the
base configuration `streams record` builds every committed stream from, and `agreement prepare` builds
its generated streams and cross-build tables from. It is edited by
hand and checked by `simcore config check`:

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- config check simcore/config/default.json
```

The command prints nothing and exits 0 for a valid file. An invalid file prints one `path: message`
line per error and exits 1.

## content/

`test-world.mmcd` is the deterministic test world, in the collision data format of
[contracts/collision-data-format.md](../specs/015-shared-simulation-core/contracts/collision-data-format.md).
Only `simcore content generate` writes it, and it reproduces the committed file byte for byte:

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- content generate --out simcore/content/test-world.mmcd
```

Every stream names the world's checksum, so when the world changes, run this first and then
regenerate the streams.

## streams/

The 19 named `.mmrs` streams, in the recorded stream format, and `REGENERATIONS.md`, the log of
every regeneration. Both are written only by `simcore streams record`, which requires a reason:

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --all --reason "Explain why the expected hashes change"
```

The regeneration rule (FR-028): the expected hashes change only through this command, and the changed
streams are committed together with the `REGENERATIONS.md` entry it appends, which gives the reason and
the old and new hashes. A run that changes nothing appends no entry. `simcore streams verify` runs
every stream through the server build and checks every expected hash, writing nothing:

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify
```

It prints one `<name>: match, <steps> steps, final <hash>` line per stream and
`streams verify: 19 streams, 19 match, 0 diverged, 0 not comparable, 0 refused`, and exits 0.

## golden/

The golden fixtures of the three formats, one folder each: `input/` (the input command format),
`collision/` (the collision data format) and `stream/` (the recorded stream format). Each case is a
`.json` and a `.bin` with the same name. Each `.json` is hand-written; each `.bin` is written only by
`simcore golden write`, from its `.json` (FR-023):

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden
```

It prints one `<format>/<case>: unchanged, <bytes> bytes` line per case and a total. After a
deliberate format change, the changed `.bin` files are committed with their `.json`.

## How the tests use this folder

Tests open everything here read-only. Tests of the writing commands (`content generate`,
`golden write` and `streams record`) run on copies in a temporary directory, so no test writes here.
`.gitattributes` marks `*.mmcd`, `*.mmrs` and `simcore/golden/**/*.bin` as binary, so git never
converts their line endings or diffs and merges them as text.
