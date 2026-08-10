# AGENTS.md（旧版备份）

> 归档：2026-08-11　|　**历史备份**，内容过时（仍引用 `.unity` 场景名、write-only Saver 等）。当前工具链约定以根目录 `AGENTS.md` 为准。

Voxel/Minecraft-style chunked terrain prototype. Runs on **Unity Tuanjie** (团结引擎), Unity `2022.3.62t10` / Tuanjie Editor 1.9.2. Single scene `Assets/Scenes/SampleScene.unity`; open and run it in the editor. There is no CLI build pipeline, no test suite, no CI — verify by entering Play Mode.

## Editor / tooling
- Scene/asset YAML uses the Tuanjie fork tag `%TAG !u! tag:yousandi.cn,2023:` — normal YAML, do not "fix" it.
- Codely (Tuanjie's AI assistant, package `cn.tuanjie.codely.bridge`) owns `.codely-cli/`, `.com-unity-codely.json` (editor↔agent bridge ports), and `.codelyignore`. Tooling, not project code — leave alone.
- No `.asmdef` files: everything compiles into `Assembly-CSharp`, and all code lives in the **global namespace** (no `namespace` blocks). Keep new scripts that way.
- Root `*.csproj` / `*.sln` are editor-regenerated and gitignored.

## Code layout (`Assets/Scripts/`)
- `World/World.cs` — chunk streaming core. On camera-chunk change it spawns background generation (`Task.Run`) and unloads chunks past `lineOfSight` (6). Built/setblock/mesh work is drained in `Update()` at per-frame caps (2 chunks / 64 block-ops / 2 mesh optimizes). Fixed seed `985211` (OpenSimplex2 + FBM → deterministic terrain).
- `World/VoxelChunk.cs` — one GameObject per chunk; builds the mesh and culls faces. At chunk borders it asks `World.Instance.TryGetBlock`, which returns `BlockType.ERROR` for unloaded neighbors → face kept.
- `World/VoxelChunkData.cs` — off-thread payload: block array plus `pendingBlocks` (cross-chunk writes, re-applied on the main thread).
- `World/Saver.cs` — **write-only** save system. Emits `.vrf` region files (32³ chunks, 4096-byte sectors, deflate-compressed `uint[4096]` indexed `(x<<8)|(y<<4)|z`) under `Application.persistentDataPath/world_saves/r.{x}.{y}.{z}.vrf`. No load path exists yet.
- `World/Block/` — `Block` is a `readonly struct` wrapping a uint (low 16 bits = type; state bits at `StateMask` 0x000F_0000, shift 16). `BlockRegistry` holds block singletons; `BlockUVMap` maps block+face → atlas tile; `MeshData` does vertex/UV math.
- `World/Position/Postions.cs` — the filename typo is real; holds `VCPosInWorld` (chunk coords), `BlockPosInWorld`, `BlockPosInVoxelChunk` and conversions (chunk size 16 ⇒ `>>4` / `& 15`).
- `Camera/CameraMove.cs` — free-fly camera (WASD + Space/Shift + mouse), main-thread only.
- `WorldManager.cs` / `DataBuffer.cs` — `DontDestroyOnLoad` singletons for the block material and the cached `(blockType, faceIndex) → UV` dictionary.
- `FastNoiseLite/` — vendored third-party MIT noise library; don't edit.

## Conventions
- Code comments and `Debug.Log` strings are written in **Chinese** — match that in new code.
- New textures: add a tile to `Assets/Atlas.png` and a row in `BlockUVMap.uvTable`; block types live in the `BlockType` enum in `Block/Block.cs`.
- When adding a block, update `BlockRegistry` as well.

## Gotchas
- `World.cs` has `using static UnityEditor.PlayerSettings;` in a runtime script — editor-only assembly, breaks standalone builds.
- `Block.GetBlockState()` is broken by operator precedence: `_value & StateMask >> StateShift` parses as `_value & 0xF` (low nibble of the type), not `(_value & StateMask) >> StateShift`. Currently unused.
- Direction/face-index labels are inconsistent: `VoxelChunk.Direction.North` = +Z (`z+1`) while `BlockUVMap.FaceIndex.North` = -Z (South = +Z). `MeshData` uses `(int)dir` directly as the UV face index. Invisible today because every side texture is identical, but it matters once directional textures exist.
- `MeshData` UV math assumes a virtual grid of 32 cells × 24px (16px tile + 4px padding) = 768px, against a real 512×512 `Atlas.png`. Leave those constants alone or texturing breaks.
- Dead code that looks live: `World.GenerateVoxelChunk` (duplicate of the `*Data` variant, unused), the entire `AsyncSaver` class in `Saver.cs` (commented out — saves run synchronously on the main thread during chunk unload), and `BasicTree.cs` (empty stub).
- `SimpleRegionWriter` opens with `FileMode.Create` (overwrites) and flushes (`fsync`) after every chunk; region files accumulate and are never read.
- `Constants.CHUNK_SIZE = 16` is also hardcoded as magic numbers in `Saver.cs` (4096 / 16³ / region 32 / index shifts); changing it requires touching many files.
