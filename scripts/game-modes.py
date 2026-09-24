"""List every game mode the installed Rust build ships, with the settings docs/game-modes.md summarizes.

server.gamemode <name> spawns assets/prefabs/gamemodes/<name>.prefab, and GameModeManifest
(assets/prefabs/gamemodes/resources/gamemodemanifest.asset) lists those prefabs by GUID. The GUIDs
resolve to paths through assets/manifest.asset in content.bundle, and the prefabs themselves sit as
GameObjects in the asset scenes of assetscenes.bundle. For each mode this prints its shortname (what
gamemode.set matches), class, title, tags, convar overrides, Facepunch's feature list, and every
BaseGameMode setting that differs from the vanilla prefab. It then decodes each ScriptableWorldConfig
in RustDedicated_Data/resources.assets, the map generation overrides WorldConfig.LoadScriptableConfigs
merges in when server.gamemode is in its GameModes list. That file ships no type trees, so the decoder
follows the field order of the ScriptableWorldConfig class.

Usage:
  uv run --with UnityPy python3 scripts/game-modes.py

Exits non-zero on a missing bundle, a missing manifest, a manifest prefab absent from the asset scenes,
or a world config whose bytes do not match the expected layout.
"""

import struct
import sys
from pathlib import Path

import UnityPy

REPO_ROOT = Path(__file__).resolve().parent.parent
CONTENT = REPO_ROOT / "server/Bundles/shared/content.bundle"
ASSET_SCENES = REPO_ROOT / "server/Bundles/shared/assetscenes.bundle"
RESOURCES = REPO_ROOT / "server/RustDedicated_Data/resources.assets"
MANIFEST_PATH = "assets/prefabs/gamemodes/resources/gamemodemanifest.asset"

# Engine bookkeeping, sounds, and fields shown separately below.
SKIP = {
    "m_GameObject", "m_Enabled", "m_Script", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier",
    "prefabID", "bounds", "impactEffect", "postNetworkUpdateComponents", "warmupMusics", "lossMusics",
    "winMusics", "raidWindowOpenSound", "raidWindowCloseSound", "shortname", "gamemodeTitle", "permanent",
    "gameModeTags", "convars", "addedFeatures", "removedFeatures", "changedFeatures", "teams", "loadouts",
    "startingLoadouts", "Restrictions",
}


def load(path: Path):
    if not path.is_file():
        raise FileNotFoundError(f"not found: {path}")
    return UnityPy.load(str(path))


def mode_prefab_paths() -> list[str]:
    env = load(CONTENT)
    if MANIFEST_PATH not in env.container:
        raise LookupError(f"{MANIFEST_PATH} not in {CONTENT}")
    guids = [ref["guid"] for ref in env.container[MANIFEST_PATH].read_typetree()["gameModePrefabs"]]
    manifest_key = next(k for k in env.container if k.lower() == "assets/manifest.asset")
    by_guid = {p["guid"]: p["name"] for p in env.container[manifest_key].read_typetree()["prefabProperties"]}
    missing = [g for g in guids if g not in by_guid]
    if missing:
        raise LookupError(f"GameModeManifest GUIDs with no manifest path: {missing}")
    return [by_guid[g] for g in guids]


def mode_settings(paths: list[str]) -> dict[str, tuple[str, dict]]:
    wanted = set(paths)
    found = {}
    env = load(ASSET_SCENES)
    for bundle in env.files.values():
        for scene in getattr(bundle, "files", {}).values():
            for obj in getattr(scene, "objects", {}).values():
                if obj.type.name != "GameObject" or obj.peek_name() not in wanted:
                    continue
                go = obj.read()
                for comp in go.m_Component:
                    reader = getattr(comp, "component", comp).deref()
                    if reader.type.name != "MonoBehaviour":
                        continue
                    tree = reader.read_typetree()
                    if "shortname" in tree:
                        script = reader.read(check_read=False).m_Script.deref().read()
                        found[go.m_Name] = (script.m_ClassName, tree)
    absent = wanted - found.keys()
    if absent:
        raise LookupError(f"game mode prefabs not found in {ASSET_SCENES.name}: {sorted(absent)}")
    return found


def features(tree: dict, key: str) -> str:
    return "; ".join(f["legacyEnglish"] for f in tree.get(key) or []) or "-"


def print_modes(paths: list[str], settings: dict[str, tuple[str, dict]]) -> None:
    vanilla = settings.get("assets/prefabs/gamemodes/vanilla.prefab", ("", {}))[1]
    for path in paths:
        cls, tree = settings[path]
        kind = "permanent" if tree["permanent"] else "match"
        print(f"== {Path(path).stem}: shortname {tree['shortname']}, {cls}, \"{tree['gamemodeTitle']}\", {kind}")
        print(f"   tags: {', '.join(tree.get('gameModeTags') or []) or '-'}")
        print(f"   convars: {'; '.join(tree.get('convars') or []) or '-'}")
        print(f"   teams: {', '.join(t['name'] for t in tree.get('teams') or []) or '-'}")
        print(f"   restrictions: {len(tree.get('Restrictions') or [])} items")
        for key in ("addedFeatures", "removedFeatures", "changedFeatures"):
            print(f"   {key}: {features(tree, key)}")
        diffs = [f"{k}={v}" for k, v in tree.items() if k not in SKIP and vanilla.get(k) != v]
        print(f"   differs from vanilla: {', '.join(diffs) or '-'}")


def print_world_configs() -> None:
    env = load(RESOURCES)
    configs = 0
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        mono = obj.read(check_read=False)
        if not mono.m_Script.m_PathID or mono.m_Script.deref().read().m_ClassName != "ScriptableWorldConfig":
            continue
        raw = obj.get_raw_data()
        pos = 28  # m_GameObject PPtr (12), m_Enabled aligned to 4, m_Script PPtr (12)

        def read_string() -> str:
            nonlocal pos
            (size,) = struct.unpack_from("<i", raw, pos)
            text = raw[pos + 4:pos + 4 + size].decode()
            pos = (pos + 4 + size + 3) & ~3
            return text

        def read_strings() -> list[str]:
            nonlocal pos
            (count,) = struct.unpack_from("<i", raw, pos)
            pos += 4
            return [read_string() for _ in range(count)]

        def read_bool() -> bool:
            nonlocal pos
            value = raw[pos] != 0
            pos += 4  # each bool field is aligned to 4
            return value

        name = read_string()
        game_modes = read_strings()
        trains = read_bool()
        labs = read_bool()
        blacklist = read_strings()
        if pos != len(raw):
            raise ValueError(f"{name}: decoded {pos} of {len(raw)} bytes; ScriptableWorldConfig changed shape")
        configs += 1
        print(f"== world config {name}: GameModes {game_modes}, UndergroundTrains {trains}, "
              f"UnderwaterLabs {labs}, BlacklistedPrefabs {blacklist}")
    if configs == 0:
        print("== no ScriptableWorldConfig assets")


def main() -> int:
    paths = mode_prefab_paths()
    print_modes(paths, mode_settings(paths))
    print_world_configs()
    return 0


if __name__ == "__main__":
    sys.exit(main())
