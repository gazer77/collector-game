# Collector

A data-driven .NET 10 engine for collect → produce → sell → expand games. The first game is a farm.

- Web: Blazor WebAssembly (`src/Collector.Web`), hosted by the server
- API: ASP.NET Core, server-authoritative, SQLite (`src/Collector.Server`)
- Shared UI: Razor components and the client game session (`src/Collector.UI`)
- Engine: pure C# simulation driven by JSON content packs (`src/Collector.Engine`, `packs/`)
- Mobile (planned): .NET MAUI Blazor Hybrid reusing `src/Collector.UI`

See [docs/DESIGN.md](docs/DESIGN.md) for the architecture and roadmap.

## Run

```
dotnet run --project src/Collector.Server --launch-profile http
```

Then open http://localhost:5128. The server serves the web client, the API and the pack files. On first run it creates `src/Collector.Server/App_Data/` with the SQLite database and a token signing key.

Press **R** to replant a field with the crop it last grew (or your most recent crop, for a field that has none yet): the selected field if it is empty, otherwise the next empty field in reading order. Buildings and pens show how many free slots they have.

Each browser gets its own guest farm (a device id kept in localStorage). The game clock only runs while the page is open.

## Test

```
dotnet test
```

## Configuration

Settings live under `Collector` in `appsettings.json` (or environment variables such as `Collector__DataDirectory`):

| Setting | Default | Meaning |
|---|---|---|
| `DataDirectory` | `App_Data` | SQLite database and signing key location |
| `PacksDirectory` | packs next to the binaries | Where content packs are loaded from |
| `SigningKey` | generated | Base64 token signing key (≥ 32 bytes) |
| `SessionTimeoutSeconds` | 45 | Gap after which a player counts as away |
| `ClockToleranceMilliseconds` | 2000 | Client clock drift the server forgives |

## Editing the farm

Everything about the farm — crops, animals, buildings, prices, timers, levels, land, equipment — is in `packs/farm/*.json`. Equipment (`upgrades.json`) is bought once and applies to named producers or whole categories: `durationMultiplier` speeds up jobs started afterwards, `bonusChance` gives a chance of one extra of each output per collected batch. Restart the server after editing; it refuses to start and lists the problems if a pack is invalid.
