# AGENTS.md

Voxel/Minecraft-style chunked terrain prototype. Runs on **Unity Tuanjie** (团结引擎), Unity `2022.3.62t10` / Tuanjie Editor 1.9.2. Single scene `Assets/Scenes/SampleScene.scene` (Tuanjie uses `.scene`, not `.unity`); open and run it in the editor. There is no CLI build pipeline, no test suite, no CI — verify by entering Play Mode.

## Editor / tooling
- Scene/asset YAML uses the Tuanjie fork tag `%TAG !u! tag:yousandi.cn,2023:` — normal YAML, do not "fix" it.
- Tuanjie rewrites asset `.meta` guid lines as **base64** (e.g. `guid: XihL5iqu...=`), while references inside `.mat`/`.scene` files use 32-hex guids derived from the same asset. They must stay consistent — re-importing an asset can regenerate the meta guid and silently break references (symptom: white rendering, no error). When a texture reference looks broken, verify the `.meta` guid matches the referencing file.
- Codely (Tuanjie's AI assistant, package `cn.tuanjie.codely.bridge`) owns `.codely-cli/`, `.com-unity-codely.json` (editor↔agent bridge ports), and `.codelyignore`. Tooling, not project code — leave alone.
- No `.asmdef` files: everything compiles into `Assembly-CSharp`, and all code lives in the **global namespace** (no `namespace` blocks). Keep new scripts that way.
- Root `*.csproj` / `*.sln` are editor-regenerated and gitignored.

## Code layout (`Assets/Scripts/`)
- `World/World.cs` — facade/coordinator (non-singleton, explicitly placed in scene, others get it via serialized reference / FindObjectOfType). Owns lifecycle, camera spawn point, saver integration, and API forwarding; delegates to the three specialized classes created in `Awake`:
  - `ChunkStreamer` — streaming scheduling: view-distance box, background generation (`Task.Run`), mesh scheduling, per-frame budget, distance-based ordering. `World.lineOfSight = 12` (horizontal), `verticalLineOfSight = 6`.
  - `TerrainGenerator` — terrain/tree generation + **save load path**: calls `saver.TryLoadVoxelChunk` first, regenerates only on miss. Fixed seed `985211` (OpenSimplex2 + FBM → deterministic terrain).
  - `ChunkStore` — chunk storage, object pooling, cross-chunk writes, unload-and-save.
- `World/Constants.cs` — central constants: `CHUNK_SIZE = 16`, `CHUNK_SIZE_LOG2 = 4`, `REGION_SIZE = 32`, `REGION_SIZE_LOG2 = 5`, `SECTOR_SIZE = 4096`, `CHUNK_VOLUME`. Use these instead of magic numbers.
- `World/VoxelChunk.cs` — one GameObject per chunk; builds the mesh and culls faces (see `ChunkMeshBuilder`). Also defines the single `Direction` enum: `East=0 +X, West=1 -X, Up=2 +Y, Down=3 -Y, South=4 -Z, North=5 +Z`.
- `World/VoxelChunkData.cs` — off-thread payload: block array plus `pendingBlocks` (cross-chunk writes, re-applied on the main thread).
- `World/Saver.cs` — full **read+write** save system. `.vrf` region files (32³ chunks/region, 4096-byte sectors, deflate-compressed `uint[CHUNK_VOLUME]` indexed `(x<<8)|(y<<4)|z`) under `Application.persistentDataPath/world_saves/r.{x}.{y}.{z}.vrf`. Async worker + backpressure queue (synchronous fallback when full) + batch flush + per-chunk retry; `TryLoadVoxelChunk` reads back with format-version validation.
- `World/Block/` — `Block` is a `readonly struct` wrapping a uint (low 16 bits = type via `BlockBits.TypeMask`; state bits at `StateMask` 0xFFFF_0000, shift 16; `StageMask` 0x3 for pea growth stage). `BlockRegistry` holds block singletons; `BlockUVMap` maps block+face → atlas tile; `MeshData` does vertex/UV math; `ChunkMeshBuilder` does face culling and emits pea cross-quads; `PeaTextures` paints pea placeholder tiles at runtime.
- `World/Position/Postions.cs` — the filename typo is real; holds `VCPosInWorld` (chunk coords), `BlockPosInWorld`, `BlockPosInVoxelChunk` and conversions (chunk size 16 ⇒ `>>4` / `& 15`).
- `Camera/CameraMove.cs` — free-fly camera (WASD + Space/Shift + mouse), main-thread only.
- `Player/BlockInteraction.cs` — raycast-based break/place. Selected item comes from `world.Backpack` (number keys 1-9 write `Backpack.Select`), pauses world ops while the backpack window is open. `PeaSeed` is a backpack item (slot 7).
- `Inventory/ItemInstance.cs` + `Inventory/Backpack.cs` — item system (2026-08-11): non-stacking item list + `SelectedIndex` (single source of truth for selection) + `BackpackOpen`. Plain class, created by `World.Awake`.
- `UI/HotbarWindow.cs` + `UI/BackpackWindow.cs` — IMGUI (OnGUI), `AddComponent` by `World` + `Init(Backpack)` injection. Atlas icons use the 24px-cell formula `(col*24+4, row*24+4, 16, 16)/768`; backpack toggles on E.
- `WorldManager.cs` / `DataBuffer.cs` — `DontDestroyOnLoad` singletons for the block material and the cached `(blockType, face) → UV` dictionary.
- `FastNoiseLite/` — vendored third-party MIT noise library; don't edit.

## Atlas / textures
- `Assets/Atlas.png` is **768×768** = a 32×32 grid of **24px cells** (16px tile + 4px padding on each side). `MeshData` UV math (`atlasSize = 512/16`, `sizePerTexture = 24`, `totalSize = 768`) matches this real layout — do not change those constants or texturing breaks.
- Cell coordinates in `BlockUVMap.uvTable` are `(col, row)` where row is counted from the texture bottom (matches Unity's v direction). Content currently lives in rows 25–31 (bottom region), so row 0 = top of that content area in practice.
- `PeaTextures.CellByStage` uses cells `(2,0) (2,1) (2,2)` and **must paint using the 24px-cell formula** `ox = c.x * 24 + 4, oy = c.y * 24 + 4` — using 16px cells puts pixels in the wrong place and the pea quads sample transparent texels (invisible, no error).
- New textures: add a tile to `Assets/Atlas.png` and a row in `BlockUVMap.uvTable`; block types live in the `BlockType` enum in `Block/Block.cs`. When adding a block, update `BlockRegistry` as well.

## Pea (豌豆) system
- Growth chain: 0=最小苗 (single-cell) → 1=苗 (single-cell) → 2=两格高植株 (two-tall) → 3=开花结果 (two-tall), stored in `BlockBits.StageMask` (bit16-17). Stage 2 renders as a **two-tall plant**: bottom cell `PeaStem` (cell `PlantBottomCell (2,5)`) + a new top-cell block type `BlockType.PeaPlantTop = 9` (cell `PlantTopCell (2,4)`; MC tall-plant style: raycast-hittable, saved, break-linked; no tile). **Stage 3 (flowering) picks cells by genome** via `PeaTextures.GetFlowerCells(genome, out bottom, out top)` — flower color (locus 2, dominant = purple) × flower position (locus 5, dominant = axillary) map to column-3 cells `(3,row)/(3,row+1)` with `row = (axillary?0:4) + (purple?0:2)` (i.e. 腋紫 (3,0)/(3,1), 腋白 (3,2)/(3,3), 顶紫 (3,4)/(3,5), 顶白 (3,6)/(3,7)). Mesh is built on a background thread, so `ChunkMeshBuilder.CreateSnapshot` copies the chunk's pea tile genomes (`TileGenomes`, keys `(x<<8)|(y<<4)|z`) plus the Y-1 neighbor's (`TileGenomesBelow`, for a `PeaPlantTop` at y=0); missing genome falls back to the flowerless cells (2,5)/(2,4). Stage 0/1 use `PeaTextures.CellByStage` cells `(2,3)/(2,2)` — the old `(2,1)/(2,0)` runtime placeholders are retired and `PaintAtlasPlaceholders` no longer paints anything (never touch cells (2,4)/(2,5)).
- All pea blocks (both `PeaStem` and `PeaPlantTop`) render as **cross-quads** via `MeshData.AddPeaQuadCell`, so **faces adjacent to them are NOT culled** — `ChunkMeshBuilder.ShouldBeEliminated` treats them like Leaves (keep neighbor faces). Keep that when editing culling logic.
- Growth is **MC-style random-tick driven at 20 ticks/sec** (`ChunkStore.TickPeaRandomTicks`, main-thread only; `World.Update` accumulates deltaTime and catches up missed ticks with a while loop). Each chunk each tick draws `PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK` (3) random positions — 1:1 with MC's 3 per section per tick, since our chunk is the same 16³ volume as a MC section — and a `PeaStem` with stage < 3 advances one stage with `PEA_GROWTH_ADVANCE_CHANCE` (1/3). Stage 1→2 first places a `PeaPlantTop` above (requires Air above; cross-chunk safe; advances bottom only after the top write succeeds); the top cell never grows. Expected ~205s per stage, ~10 min to full ripeness. See `docs/design/GROWTH_RANDOM_TICK.md`.
- Break linkage (`BlockInteraction.TryBreakBlock`): breaking a bottom cell (stage ≥ 2) also clears the top `PeaPlantTop` above and removes its tile; breaking a top cell reverts the `PeaStem` below to stage 0 (tile kept, can regrow).
- Support check (in `BlockUpdateCenter.DispatchBlockUpdate`): a `PeaStem` drops when its below cell is Air — triggered by `NeighborChanged` (digging out the block under it) or `Place` (planting in mid-air). Drop = block + tile removed, top cell cleared via the break linkage above; there is no item-drop entity system, the plant simply disappears.
- Old saves with stage ≥ 2 peas get their top cells repaired on load by `ChunkStore.RepairPeaPlants` (called after chunk creation; fills missing tops, clears orphan tops, covers the cross-chunk y=15→y=0 case).

## Block updates (方块更新机制)
- `BlockUpdateCenter`（main-thread, non-MonoBehaviour, owned by `World`) is the single dispatch hub for three update types: **random tick** (20Hz, per chunk per tick 3 random positions → `DispatchRandomTick` by block type), **block update** (block change → self + 6 neighbors → `DispatchBlockUpdate(pos, source)`; `BlockUpdateSource { Place, Break, StateChange, NeighborChanged }`), **scheduled tick** (`ScheduleTick(pos, delayTicks)`, per-chunk pending list, dropped on chunk unload; not persisted).
- Runtime writes must go through `ChunkStore.SetBlock` (cross-chunk safe; old==new skips write and notifications; recursion depth capped by `MAX_BLOCK_UPDATE_DEPTH`). Generation-time writes (background `data.Setblock` → pendingBlocks replay) must NOT trigger updates (suppress path). `vc.SetBlock` is the low-level write used only inside `ChunkStore`.
- Pea break linkage lives in the update dispatch, not in `BlockInteraction` (which only sets Air + requests mesh rebuild): breaking a `PeaPlantTop` notifies below → `PeaStem` reverts to stage 0 (tile kept); breaking a `PeaStem` (stage ≥ 2) notifies above → top cell cleared. Mesh rebuild (`changed` → next-frame rebuild) is render-layer and decoupled from logic updates.
- New blocks: add a branch in the `BlockUpdateCenter` dispatch switch (random tick / neighbor changed / scheduled tick as needed). See `docs/design/BLOCK_UPDATE_SYSTEM.md`.

## Conventions
- Code comments and `Debug.Log` strings are written in **Chinese** — match that in new code.
- Save/region/layout constants go in `Constants.cs`; avoid hardcoding 16 / 4096 / 32 in new code.

## Gotchas
- Re-importing `Atlas.png` (or any asset whose guid is referenced by a `.mat`/`.scene`) can regenerate the meta guid and silently break references → white rendering, no error. Fix by pointing the reference at the current meta guid.
- `MeshData` UV math and `PeaTextures` pixel math both use the 24px-cell grid (16px tile + 4px padding). Any new atlas-writing code must use `x*24 + 4`, not `x*16`.
- `Block.GetBlockState()` was historically broken by operator precedence; it is now fixed as `(_value & BlockBits.StateMask) >> BlockBits.StateShift` — keep the parens if you touch it.
- `SimpleRegionWriter` opens with `FileMode.Create` (overwrites the whole region file on first write) and flushes on a batch cadence, not per chunk. Region files are read back on load; treat them as the source of truth for persistence.
- Dead code to watch for: none known currently — the old duplicate `GenerateVoxelChunk`, `AsyncSaver`, and `BasicTree.cs` stub have all been removed. If you see them reappear, they're not needed.
