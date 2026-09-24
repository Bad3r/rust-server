"""List free item skins (UnlockedByDefault=true on an ItemSkin asset, granted to every
player without a Steam ownership check) and DLC-tagged assets (dlcAppID with the per-item
bypassLicenseCheck override) across the given bundles.

See PlayerBlueprints.HasUnlocked / ItemPassesDlcChecks and ItemSkin.HasUnlocked /
SteamDLCItem.HasLicense in Assembly-CSharp for the code paths these flags feed.

Usage:
  uv run --with UnityPy python3 scripts/skin-freeitems.py [bundle...]

Defaults to content.bundle, skinnables.preload.bundle, and items.preload.bundle under
server/Bundles/shared relative to the repo root. Exits non-zero on a missing bundle or a
UnityPy load failure; a per-asset typetree read failure is reported to stderr and skipped,
since a handful of unrelated asset types in these bundles are not readable this way.
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


def scan(bundle: Path) -> int:
    if not bundle.is_file():
        raise FileNotFoundError(f"bundle not found: {bundle}")
    env = UnityPy.load(str(bundle))
    seen = 0
    for path, reader in env.container.items():
        if not path.lower().endswith(".asset") or reader.type.name != "MonoBehaviour":
            continue
        try:
            tree = reader.read_typetree()
        except Exception as exc:  # noqa: BLE001 - report and keep scanning other assets
            print(f"# unreadable {path}: {exc}", file=sys.stderr)
            continue
        seen += 1
        if tree.get("UnlockedByDefault"):
            print(
                f"FREE_ITEMSKIN\t{path}\tid={tree.get('id')}\titemname={tree.get('itemname')}"
                f"\tname={tree.get('m_Name')}"
            )
        if "dlcAppID" in tree:
            print(f"DLC\t{path}\tappid={tree.get('dlcAppID')}\tbypassLicenseCheck={tree.get('bypassLicenseCheck')}")
    print(f"# {seen} MonoBehaviour .asset entries scanned in {bundle}", file=sys.stderr)
    return seen


def main(argv: list[str]) -> int:
    bundles = [Path(b) for b in argv] or DEFAULT_BUNDLES
    total = sum(scan(b) for b in bundles)
    if total == 0:
        print("no MonoBehaviour .asset entries scanned in any bundle", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
