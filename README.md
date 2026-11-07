<div align="center">
  <h1><strong>Ranks</strong></h1>
  <p>Points, ranks and gameplay statistics for CS2 — with a module API other plugins build on.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <a href="https://github.com/shmitzas/Ranks/releases/latest"><img src="https://img.shields.io/github/v/release/shmitzas/Ranks?style=flat&logo=github&cacheSeconds=3600" alt="Latest Release"></a>
  <img src="https://img.shields.io/github/downloads/shmitzas/Ranks/total?style=flat&logo=github&cacheSeconds=3600" alt="Downloads">
  <img src="https://img.shields.io/github/stars/shmitzas/Ranks?style=flat&logo=github&cacheSeconds=3600" alt="Stars">
</p>

> [!NOTE]
> **Migrating from another ranking plugin?**<br>
> [Ranks_Importer](https://github.com/shmitzas/PluginDataImporters)
> is a separate one-shot tool that imports from:
> - **[LVL Ranks](https://github.com/Pisex/cs2-lvl_ranks)** - by [Pisex](https://github.com/Pisex) — the original, plus its [modules](https://github.com/Pisex/cs2-lvl_ranks_modules)
> - **[K4-LevelRanks-SwiftlyS2](https://github.com/K4ryuu/K4-LevelRanks-SwiftlyS2)** - by [K4ryuu](https://github.com/K4ryuu) — now archived
> - **[K4-LevelRanks-Upkeep](https://github.com/shmitzas/K4-LevelRanks-Upkeep)** - by [shmitzas](https://github.com/shmitzas) — the maintained fork, with minimal changes to original fork
> - **[K4-System](https://github.com/karola3vax/K4-System)** - by [karola3vax](https://github.com/karola3vax) — a rewrite of K4ryuu's original
> - **[K4-Zenith](https://github.com/K4ryuu/K4-Zenith)** - by [K4ryuu](https://github.com/K4ryuu) — its Ranks, Stats and TimeStats modules

---

## ✨ Features

All of them included in the Ranks plugin. There are also [Modules](#-modules) which add even more features.

- 🏆 **Points and ranks** — a configurable ladder, priced per event, with VIP and dynamic multipliers
- 📊 **Gameplay stats** — kills, deaths, assists, round and match results, bomb plants and defuses
- ⏱️ **Playtime** — time on the server as K4-LevelRanks counted it, with `!mytime` and a playtime leaderboard, `!ttop`
- 🗓️ **Seasons** — optional: every calendar month or every N days everyone's rank starts over, while past seasons and all-time standings stay browsable
- 🎖️ **Native scoreboard ranks** — Premier, Competitive, Wingman, or your own icon set, plus rank clan tags
- 👀 **Everyone's rank, not just yours** — the reveal CS2 sends once per map, re-sent on round start, on join and on rank change
- 🗄️ **MySQL, MariaDB, PostgreSQL or SQLite** — one connection, shared with every module
- 🧩 **Module API** — other plugins read ranks, react to changes, and own tables core migrates for them
- 🔁 **Live config reload** — points, ladder and scoreboard apply on save; only command names and table affixes need a restart
- 🏷️ **Multi-server friendly** — table prefixes and suffixes let several servers share one database

## 🚀 Getting Started

| Guide | Description |
|-------|-------------|
| **[Features](../../wiki/Features)** | The full list, and what core deliberately does not do |
| **[Installation](../../wiki/Installation)** | Install, pick a database, verify |
| **[Commands](../../wiki/Commands)** | Player and admin commands |
| **[Menus](../../wiki/Menus)** | The one menu tree, and what each category holds |
| **[Configuration](../../wiki/Configuration)** | Every setting, with defaults |

## ⚙️ Tuning Rank Progression

| Topic | Description |
|-------|-------------|
| **[Points and Events](../../wiki/Points-And-Events)** | Price every action, and the gates that suppress them |
| **[Rank Ladder](../../wiki/Rank-Ladder)** | Turn points into named tiers |
| **[Scoreboard and Rank Icons](../../wiki/Scoreboard-And-Rank-Icons)** | Five modes, plus your own icon set |
| **[Database](../../wiki/Database)** | Shared ladders, seasons, purging, persistence |

## 🧩 Modules

Official modules. **[Modules Overview](../../wiki/Modules)** explains what a module is, the four kinds, and which can conflict.

| Module | Kind | What it adds |
|--------|------|--------------|
| **[Ranks_WeaponStats](../../wiki/Module-WeaponStats)** | data | Per-weapon and per-hitbox statistics, with `!weaponstats` |
| **[Ranks_EloPoints](../../wiki/Module-EloPoints)** | absolute | Faceit-style Elo rating in place of per-kill scoring |
| **[Ranks_CustomCombos](../../wiki/Module-CustomCombos)** | additive | Operator-defined kill combinations, with `!combos` |
| **[Ranks_Prestige](../../wiki/Module-Prestige)** | display | Prestige levels as a virtual point deduction, with `!prestige` |

---

## 📚 Reference

| Topic | Description |
|-------|-------------|
| **[API Reference](../../wiki/API-Reference)** | Every member of `IRanksApi` |
| **[API Integration](../../wiki/API-Integration)** | Build your own module |
| **[Building](../../wiki/Building)** | Build from source and release |
| **[Troubleshooting](../../wiki/Troubleshooting)** | Symptoms, causes and fixes |

---

## 🤝 Report issues or request features

- **Issues**: [GitHub Issues](https://github.com/shmitzas/Ranks/issues)
