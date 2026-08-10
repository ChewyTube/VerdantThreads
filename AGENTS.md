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
- `Player/BlockInteraction.cs` — raycast-based break/place, number keys 1-9 to select block, `PeaSeed` in the placeable list.
- `WorldManager.cs` / `DataBuffer.cs` — `DontDestroyOnLoad` singletons for the block material and the cached `(blockType, face) → UV` dictionary.
- `FastNoiseLite/` — vendored third-party MIT noise library; don't edit.

## Atlas / textures
- `Assets/Atlas.png` is **768×768** = a 32×32 grid of **24px cells** (16px tile + 4px padding on each side). `MeshData` UV math (`atlasSize = 512/16`, `sizePerTexture = 24`, `totalSize = 768`) matches this real layout — do not change those constants or texturing breaks.
- Cell coordinates in `BlockUVMap.uvTable` are `(col, row)` where row is counted from the texture bottom (matches Unity's v direction). Content currently lives in rows 25–31 (bottom region), so row 0 = top of that content area in practice.
- `PeaTextures.CellByStage` uses cells `(2,0) (2,1) (2,2)` and **must paint using the 24px-cell formula** `ox = c.x * 24 + 4, oy = c.y * 24 + 4` — using 16px cells puts pixels in the wrong place and the pea quads sample transparent texels (invisible, no error).
- New textures: add a tile to `Assets/Atlas.png` and a row in `BlockUVMap.uvTable`; block types live in the `BlockType` enum in `Block/Block.cs`. When adding a block, update `BlockRegistry` as well.

## Pea (豌豆) system
- `BlockType.PeaStem` is rendered as a **cross-quad** (two XZ-diagonal billboards) via `MeshData.AddPeaQuad`, height and texture cell vary by growth stage (0=苗/1=开花/2=结荚, stored in `BlockBits.StageMask`).
- The cross doesn't fill its cell, so **faces adjacent to a PeaStem are NOT culled** — `ChunkMeshBuilder.ShouldBeEliminated` treats PeaStem like Leaves (keep neighbor faces). Keep that when editing culling logic.
- Stage data and visuals exist, but there is **no growth logic yet** (nothing advances stage over time).

## Conventions
- Code comments and `Debug.Log` strings are written in **Chinese** — match that in new code.
- Save/region/layout constants go in `Constants.cs`; avoid hardcoding 16 / 4096 / 32 in new code.

## Gotchas
- Re-importing `Atlas.png` (or any asset whose guid is referenced by a `.mat`/`.scene`) can regenerate the meta guid and silently break references → white rendering, no error. Fix by pointing the reference at the current meta guid.
- `MeshData` UV math and `PeaTextures` pixel math both use the 24px-cell grid (16px tile + 4px padding). Any new atlas-writing code must use `x*24 + 4`, not `x*16`.
- `Block.GetBlockState()` was historically broken by operator precedence; it is now fixed as `(_value & BlockBits.StateMask) >> BlockBits.StateShift` — keep the parens if you touch it.
- `SimpleRegionWriter` opens with `FileMode.Create` (overwrites the whole region file on first write) and flushes on a batch cadence, not per chunk. Region files are read back on load; treat them as the source of truth for persistence.
- Dead code to watch for: none known currently — the old duplicate `GenerateVoxelChunk`, `AsyncSaver`, and `BasicTree.cs` stub have all been removed. If you see them reappear, they're not needed.
