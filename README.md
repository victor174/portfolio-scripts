# Unity Game Developer Portfolio — Production Systems (Multiplayer + UI + Addressables + Gameplay)

This repository contains curated Unity code samples extracted from a larger production codebase.  
The goal is to showcase **architecture**, **performance-minded patterns**, and **real shipping systems**.

> Note: These scripts are isolated for portfolio purposes. Any sensitive endpoints, IDs, or internal references may be anonymized or simplified.

---

## What This Portfolio Demonstrates
- **Production architecture**: clear separation of responsibilities, managers, reusable utilities.
- **Async stability**: online flows, state handling, and error paths.
- **Performance & memory**: caching, on-demand loading, correct Addressables releasing.
- **UI in production**: data-driven UI flows (leaderboards), pooling-based list rendering, and state-driven refresh.
- **Gameplay systems**: wave/progression logic designed for iteration and extensibility.

---

## Selected Code Samples

### 1) Matchmaking Flow — `MatchmakerManager.cs`
**What it shows**
- Production-minded matchmaking flow (state machine patterns, async control flow, error handling).
- Typical integration approach for online services.

---

### 2) Lobby System — `PrivateLobbyManager.cs`
**What it shows**
- Lobby creation/join flow, player refresh, and state transitions.
- Coordination between UI/state and network logic.

---

### 3) Leaderboard UI Controller — `GoldenArenaRankingController.cs`
**What it shows**
- Data-driven leaderboard UI fed by backend responses (game-specific, global, and tournament modes).
- Pooling-based list rendering + clean UI rebuild flow (clear → mount).
- Dropdown-driven mode switching + “current player” highlight logic.

---

### 4) Dynamic Prefab Loading Utilities — `DynamicPrefabLoadingUtilities.cs`
**What it shows**
- Reusable utilities for dynamic loading (modular approach).
- Lifecycle patterns (load / reuse / release) and clean helper abstractions.

---

### 5) Dynamic Server Loader — `ServerDynamicLoader.cs`
**What it shows**
- Server/client coordination for dynamic content loading.
- Synchronization and robust control flow around runtime-loaded content.

---

### 6) Addressables Sprite Loader + Cache — `ImageManagerBundle.cs` + `AddressablesCache.cs`
**What it shows**
- On-demand async loading (reduces stutter).
- **Caching** + **correct releasing** to prevent memory leaks.
- Practical pattern for UI/shop/inventory dynamic icons.

---

### 7) Gameplay Wave System — `SinglePlayerWaveManager.cs`
**What it shows**
- Wave progression, conditions, state handling, and scaling logic.
- Clean gameplay system built to be extended and tuned over time.

---

## Suggested Reading Order
1. `MatchmakerManager.cs`
2. `PrivateLobbyManager.cs`
3. `GoldenArenaRankingController.cs`
4. `DynamicPrefabLoadingUtilities.cs`
5. `ServerDynamicLoader.cs`
6. `AddressablesCache.cs` + `ImageManagerBundle.cs`
7. `SinglePlayerWaveManager.cs`

---

## Dependencies (High-Level)
These samples are meant for Unity projects that commonly include:
- A multiplayer/netcode stack (project-dependent)
- Unity Addressables
- Unity Localization (for UI label tables, project-dependent)
- Unity Services (Lobby/Matchmaking) for online projects

If you want to compile these samples inside a blank Unity project, you may need to recreate minimal dependencies or replace project-specific parts with stubs/mocks.
