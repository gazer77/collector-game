# Collector Engine — Design

A data-driven .NET 10 engine for "collect → produce → sell → expand" games, with a farm game as the first content pack.

## 1. Decisions (from the kickoff interview, 2026-10-11)

| Area | Decision |
|---|---|
| Gameplay loop | The engine supports **real-time**, **idle**, **hybrid**, and **turn-based** loops through pack config. The farm starts as a **real-time farm sim** (Hay Day style). |
| Client | **Blazor WebAssembly** now. **.NET MAUI Blazor Hybrid** later for iOS/Android. All UI lives in a shared Razor Class Library. |
| Authority | **Server-authoritative** ASP.NET Core API. The client runs the same engine for instant (predicted) feedback. |
| Database | **SQLite** via EF Core to start. The provider can be swapped (Postgres) later. |
| Auth | **Guest first**: device ID → JWT. An account can be linked later (email or OAuth) to keep progress. |
| Content | **JSON game packs**, validated on load. |
| Art | Every visual is an **asset key** resolved through a per-pack manifest. Emoji/SVG placeholders first, so art can be swapped without code changes. |
| Production | **Raw + processing** (wheat → flour, milk → cheese). |
| Selling | **Order board**, **dynamic prices**, **player trading**. |
| Progression | **XP levels** unlock content, **buy land**, **upgrade buildings**, **hire workers** (automation). |
| Storage | **Capped and upgradeable** (e.g. barn for goods, silo for crops). |
| Currencies | **Configurable per pack**. The farm uses coins + gems (gems for speed-ups and rare purchases). |
| Social | Later: **visit neighbors**, **leaderboards**, **co-op/guilds**. |
| Offline | **Paused while away**: the game clock only advances during an active session. This is a pack/server setting, so other packs can choose "keeps running" or "runs up to N hours". |
| Goal | Undecided between hobby and publishing, so monetization hooks stay possible but aren't built. |

## 2. Solution layout

```
Collector.slnx
├─ src/
│  ├─ Collector.Engine        Pure C# simulation: definitions, state, commands, rules. No I/O, no ASP.NET, no Blazor.
│  ├─ Collector.Contracts     API DTOs shared by server and clients (commands envelope, sync responses).
│  ├─ Collector.Server        ASP.NET Core API: auth, persistence (EF Core/SQLite), command processing, trading, game clock.
│  ├─ Collector.UI            Razor Class Library: all game screens/components, asset resolver, API client. Shared web + mobile.
│  ├─ Collector.Web           Blazor WASM host (thin): bootstraps Collector.UI.
│  └─ Collector.Mobile        (later) MAUI Blazor Hybrid host (thin): bootstraps Collector.UI.
├─ packs/
│  └─ farm/                   JSON content pack + assets for the farm game.
├─ tests/
│  └─ Collector.Engine.Tests  Deterministic simulation tests driven by test packs.
└─ docs/
```

**Rule:** game-specific words (wheat, cow, barn) never appear in C# code. They live only in `packs/`. A second game, such as an orchard or fishing game, should need a new pack and nothing else.

## 3. Engine model

### 3.1 Definitions (loaded from a pack, immutable)

| Concept | Purpose | Farm examples |
|---|---|---|
| `Currency` | Spendable balance | coins, gems |
| `Item` | Anything that can sit in storage | wheat, milk, flour, cheese |
| `Storage` | Capacity bucket that items are assigned to, with upgrade tiers | silo (crops), barn (goods) |
| `Producer` | Placeable thing that runs recipes in slots, either all at once (`parallel`) or as a queue (`sequential`) that runs `lanes` jobs at a time, set per tier | field (parallel), chicken coop (parallel), feed mill (sequential), dairy (sequential) |
| `Recipe` | Inputs → outputs over a duration, plus XP | `plant_wheat`: 1 wheat → 2 wheat, 2 min |
| `Upgrade` | Tiered modifiers on a producer or storage | +1 slot, ×0.9 duration, +25 capacity |
| `LandParcel` | Purchasable map region with cost and level gate | parcel_n1 |
| `Level` | XP thresholds + currency and item rewards | level 2 grants 3 corn to plant |
| `OrderBoard` | Generator rules for bundle orders | 3 slots, refresh 15 min |
| `Market` | Base prices + dynamic price model | elasticity, recovery rate |
| `Worker` | Automation rule (auto-collect / auto-restart) | farmhand |
| `ClockMode` | `RealTime`, `Idle`, `Turn` + offline policy | RealTime, offline = Paused |

Crops, animals, and buildings all use the same `Producer` + `Recipe` mechanism. A field is a producer whose recipe consumes a seed item. A coop is a producer whose recipe consumes feed. They differ only in data and asset keys, so the engine never special-cases them.

### 3.2 State (per player, serializable)

```
PlayerState
  Version, GameClock (game time elapsed, not wall time), RngSeed
  Level, Xp
  Wallet          { currencyId → amount }
  Inventory       { itemId → qty }      (capacity enforced per Storage)
  StorageTiers    { storageId → tier }
  Placements      [ { id, producerId, x, y, tier, slots: [ { recipeId, startedAt, endsAt } ] } ]
  OwnedParcels    [ parcelId ]
  Orders          [ { id, requirements, reward, expiresAt } ]
  Workers         [ ... ]               (M2)
```

All timers are stored as **game-clock timestamps**, never countdowns. The state is evaluated lazily: `Advance(state, now)` resolves anything due. Because of this, "paused while away" just means the server stops advancing `GameClock` when no session heartbeat is live.

### 3.3 Commands

The only way to change state:

```
Result Apply(PlayerState state, ICommand cmd, GameContext ctx)   // ctx = definitions + clock + rng
```

`PlaceProducer`, `MoveProducer`, `StartRecipe`, `Collect`, `FillOrder`, `SellToMarket`, `BuyParcel`, `Upgrade`, `SpeedUp`, `HireWorker`, …

Commands are **deterministic**. Randomness comes from a seeded RNG stored in the state, so the client and server compute identical results.

### 3.4 Clock modes (how one engine covers different loops)

| Mode | How time advances | Used by |
|---|---|---|
| `RealTime` | Game clock follows server time while a session is active (or always, per the offline policy) | Farm (MVP) |
| `Idle` | Same as RealTime, plus producers can be set to `autoRestart`/`autoCollect` and offline accrual is enabled | Future idle packs |
| `Turn` | The clock advances only on an `EndTurn` command. Durations are measured in turns. | Future turn packs |

The hybrid loop is RealTime plus workers. It needs no fourth mode.

## 4. Client / server flow

1. The client starts, then `POST /auth/guest {deviceId}` returns a JWT. The device ID is stored locally.
2. `GET /game/{packId}/state` returns the state + pack hash. The client loads the same pack (served by the API).
3. Each player action is applied **locally first** (optimistic UI), then queued and sent: `POST /game/commands [{seq, cmd}]`.
4. The server applies the commands against its own state using **server time**, persists it, and returns `{version, stateHash}`. If the hash doesn't match, the client replaces its local state with the server's copy.
5. A heartbeat (SignalR or periodic POST) keeps the session "active" for the paused-while-away clock.

The client's clock will run slightly ahead of or behind the server's. If a command arrives a moment before the server thinks a job is done (e.g. `Collect`), the server may advance that player's clock by a small tolerance (default 2 s, repaid from later catch-ups so it can never accumulate) before applying it. A command that is still invalid after that is rejected, and the client resyncs.

Persistence (M1): table `Players` (Id, DeviceId, CreatedAt), table `Saves` (PlayerId, PackId, StateJson, Version, LastSeenAt, AheadMs). The schema is created with `EnsureCreated`; switch to EF migrations before the first schema change on a real deployment. Trading, leaderboards, and guilds get relational tables in later milestones, because they span players.

The client (`GameSession`) always shows *last server state + time since + unconfirmed commands*, rebuilt every second, so server corrections never leave the UI out of step. Pending commands are sent in order, one batch at a time, and kept through connection drops.

## 5. Content packs

```
packs/farm/
  pack.json         id, name, version, clock mode, offline policy, starting state
  currencies.json
  items.json
  storage.json
  producers.json    includes upgrade tiers
  recipes.json
  levels.json
  land.json
  orders.json
  market.json
  assets/manifest.json   assetKey → { emoji, image, tint }   (keys by convention: item.wheat, producer.dairy, currency.coins)
```

Loading has two passes: JSON-schema validation, then semantic validation. Semantic validation checks that every referenced ID exists, that no recipe can be unlocked before its producer, and so on. It fails with readable errors.

## 6. Rendering & theming

- The farm is a **grid map** rendered with absolutely positioned elements in a scrollable/zoomable container (SVG/DOM). Canvas is only needed if performance demands it later.
- `IAssetResolver` maps an asset key to markup (emoji span, `<img>`, SVG sprite). The packs' `manifest.json` drives it.
- Colors and fonts come from CSS variables in a pack `theme.css`.
- Mobile-friendly from the start: touch targets ≥ 44px, pinch-zoom on the map, no hover-only interactions.

## 7. Milestones

| Milestone | Scope |
|---|---|
| **M0 — Skeleton** ✅ | Solution, pack loader + validation, engine core (state, commands, clock), farm pack content, 37 tests. |
| **M1 — Vertical slice** ✅ | Farm pack (3 crops, chickens/cows/pigs, feed mill, dairy, smokehouse), order board, XP levels, buying land, capped storage + upgrades, coins + gems. Guest auth (JWT), SQLite, paused-while-away clock with drift tolerance, playable web client with local prediction. 46 tests. |
| **M2 — Economy** | Dynamic market prices, player trading (listings), building upgrades beyond tier 1, workers/automation. |
| **M3 — Social** | Visit neighbors (read-only + help actions), leaderboards, guilds/co-op goals. |
| **M4 — Mobile** | MAUI Blazor Hybrid host, local notifications, store builds. |
| **M5 — Second pack** | A second theme (e.g. fishing or an orchard) using a different clock mode, to prove the engine is generic. |

Hosting is local development for now. Later it can run on the home server (jacktheripper) as a systemd service behind Caddy (e.g. `collector.local`).

## 8. Open questions (not blocking M1)

- Game/brand name?
- Can trading be done between farm and non-farm packs, or is each pack its own economy? (Assumed: separate.)
- Monetization (ads/IAP for gems) if published.
- Whether gem speed-ups should exist when offline is "paused" (they feel less needed).
