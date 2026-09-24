"""Cross-check assets/skins.asset (the game's full skin directory) against the ItemSkin
MonoBehaviour assets found in the given bundles, to see how many skins.asset entries a
static bundle scan can resolve to a known UnlockedByDefault (free-skin) state.

Usage:
  uv run --with UnityPy python3 scripts/skin-coverage.py [bundle...]

Defaults to content.bundle, skinnables.preload.bundle, and items.preload.bundle under
server/Bundles/shared relative to the repo root. Exits non-zero on a missing bundle, a
UnityPy load failure, or an empty assets/skins.asset directory (a sign the asset path
changed). A per-asset typetree read failure is reported to stderr and skipped.
"""

import sys
from pathlib import Path

import UnityPy

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_BUNDLES = [
    REPO_ROOT / "server/Bundles/shared/content.bundle",
    REPO_ROOT / "server/Bundles/shared/skinnables.preload.bundle",
    REPO_ROOT / "server/Bundles/shared/items.preload.bundle",
]


def scan(bundle: Path, itemskins: dict, directory: list) -> int:
    if not bundle.is_file():
        raise FileNotFoundError(f"bundle not found: {bundle}")
    env = UnityPy.load(str(bundle))
    seen = 0
    for path, reader in env.container.items():
        low = path.lower()
        if not low.endswith(".asset") or reader.type.name != "MonoBehaviour":
            continue
        try:
            tree = reader.read_typetree()
        except Exception as exc:  # noqa: BLE001 - report and keep scanning other assets
            print(f"# unreadable {path}: {exc}", file=sys.stderr)
            continue
        seen += 1
        if low == "assets/skins.asset":
            directory.extend(
                (s.get("id"), s.get("itemid"), s.get("name"), s.get("isSkin")) for s in tree.get("skins") or []
            )
        if "UnlockedByDefault" in tree:
            itemskins[low] = bool(tree["UnlockedByDefault"])
    print(f"# {seen} MonoBehaviour .asset entries scanned in {bundle}", file=sys.stderr)
    return seen


def main(argv: list[str]) -> int:
    bundles = [Path(b) for b in argv] or DEFAULT_BUNDLES
    directory: list = []
    itemskins: dict = {}
    for bundle in bundles:
        scan(bundle, itemskins, directory)

    if not directory:
        print("assets/skins.asset was not found in any given bundle", file=sys.stderr)
        return 1

    names = {(n or "").lower() for _, _, n, _ in directory}
    found = names & set(itemskins)
    print(f"skins.asset entries: {len(directory)}, distinct names: {len(names)}")
    print(f"ItemSkin assets read: {len(itemskins)}, free: {sum(itemskins.values())}")
    print(f"skins.asset names resolved to a scanned ItemSkin: {len(found)}")
    missing = sorted(names - set(itemskins))
    print(f"unresolved names: {len(missing)}")
    for n in missing:
        print(f"  {n}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
