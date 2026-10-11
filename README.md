# Collector

A data-driven .NET 10 engine for collect → produce → sell → expand games. The first game is a farm.

- Web: Blazor WebAssembly (`src/Collector.Web`)
- API: ASP.NET Core, server-authoritative (`src/Collector.Server`)
- Engine: pure C# simulation driven by JSON content packs (`src/Collector.Engine`, `packs/`)
- Mobile (planned): .NET MAUI Blazor Hybrid sharing `src/Collector.UI`

See [docs/DESIGN.md](docs/DESIGN.md) for the architecture and roadmap.

## Build

```
dotnet build
dotnet test
```
