"""List BuildingGrade assets (assets/content/properties/buildinggrade/*.asset) with their
skin id and alwaysUnlock flag, straight from the game's content.bundle.

alwaysUnlock true hands that grade's building skin to every player regardless of Steam
ownership. false means BuildingBlock.DoUpgradeToGrade (Assembly-CSharp, RPC_Server) checks
player.blueprints.steamInventory.HasItem(skin) first and blocks the upgrade if it is false.

Usage:
  uv run --with UnityPy python3 scripts/skin-grades.py [bundle...]

Defaults to server/Bundles/shared/content.bundle relative to the repo root when no bundle
is given. Exits non-zero on a missing bundle, a UnityPy load failure, or zero BuildingGrade
hits (a sign the asset path or type changed under us).
"""

import sys
from pathlib import Path

import UnityPy

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_BUNDLES = [REPO_ROOT / "server/Bundles/shared/content.bundle"]


def scan(bundle: Path) -> int:
    if not bundle.is_file():
        raise FileNotFoundError(f"bundle not found: {bundle}")
    env = UnityPy.load(str(bundle))
    hits = 0
    for path, reader in env.container.items():
        low = path.lower()
        if not low.endswith(".asset") or "grade" not in low:
            continue
        if reader.type.name != "MonoBehaviour":
            continue
        tree = reader.read_typetree()
        if "alwaysUnlock" not in tree:
            continue
        hits += 1
        print(
            f"{path}\tname={tree.get('m_Name')}\ttype={tree.get('type')}\tskin={tree.get('skin')}"
            f"\tenabledInStandalone={tree.get('enabledInStandalone')}\talwaysUnlock={tree.get('alwaysUnlock')}"
        )
    print(f"# {hits} BuildingGrade assets in {bundle}", file=sys.stderr)
    return hits


def main(argv: list[str]) -> int:
    bundles = [Path(b) for b in argv] or DEFAULT_BUNDLES
    total = sum(scan(b) for b in bundles)
    if total == 0:
        print("no BuildingGrade assets found; asset path or type likely changed", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
