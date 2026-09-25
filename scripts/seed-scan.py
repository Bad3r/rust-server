# /// script
# requires-python = ">=3.14"
# dependencies = ["websockets", "pillow", "lz4", "numpy"]
# ///
"""Preview procedural map seeds on throwaway vanilla servers, to choose one before a wipe.

For each seed this boots server/RustDedicated without Carbon under its own identity (seedscan<N>) and ports, with
server.autouploadmap 0 and aimanager.nav_disable 1 so it neither uploads the map to Facepunch nor builds a navmesh.
Each worker flocks its slot, so concurrent runs take distinct identities and ports. Once the log shows "Server
startup complete" it saves under seedscan/<buildid>/<size>/<seed>/, buildid being the installed server build:

  monuments.json  every row of world.monuments: type, display name, prefab, and world position
  map.png         world.rendermap at --scale (north up, with world.rendermap's fixed 500 px ocean margin)
  stats.json      land share, dominant-biome shares of the land, and path counts, read from the generated .map
  server.log      the throwaway server's log; stdout.log holds its native Steam output

It then kills the server, deletes that seed's world files from the identity, and rebuilds index.html in the same
directory: a sortable summary and every map annotated with monument labels, the in-game grid, and the above-ground
railway. Seeds already scanned on this build are skipped, so an interrupted run resumes, and after a Rust update
the same seeds generate afresh. It runs next to the live server and never touches server/server/pve.

Usage:
  uv run scripts/seed-scan.py [--size N] [--jobs N] [--random N] [SEED ...]
  uv run scripts/seed-scan.py --report [--size N]    rebuild index.html for the installed build
  uv run scripts/seed-scan.py --stats FILE.map       print stats.json for any procedural .map file

--size defaults to +server.worldsize in start.sh. Each server holds about 3.5 GB of RAM at worldsize 3500, and a
seed takes 3.5 to 7 minutes there (4.5 to 8 at 4500), depending on how many servers share the CPU.
"""

import argparse
import asyncio
import fcntl
import html
import itertools
import json
import os
import re
import secrets
import shutil
import signal
import socket
import struct
import sys
import time
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import lz4.block
import numpy as np
import websockets
from PIL import Image, ImageDraw, ImageFont

REPO = Path(__file__).resolve().parent.parent
SERVER = REPO / "server"
OUT = REPO / "seedscan"
BASE_PORT = 28115  # slot N: game port BASE_PORT + 10 N, WebRCON one above
MARGIN = 500  # world.rendermap always passes MapImageRenderer.Render's default oceanMargin
GRID = 146.28572  # MapHelper.PositionToGrid
BIOMES = ("arid", "temperate", "tundra", "arctic", "jungle")  # TerrainBiome *_IDX order
OCEAN = 128  # TerrainTopology.OCEAN
# Named world.monuments rows that every map has in numbers; counted on each card but not labeled or ranked.
MINOR = (
    "Substation", "Water Well", "Underground Cave", "Train Tunnel", "Train Tunnel Link", "Ice Lake", "Wild Swamp",
    "Jungle Swamp",
)  # fmt: skip


def default_size() -> int:
    m = re.search(r"\+server\.worldsize (\d+)", (REPO / "start.sh").read_text())
    if not m:
        sys.exit("error: start.sh has no +server.worldsize; pass --size")
    return int(m[1])


def results_dir(size: int) -> Path:
    # Keyed by build: an update that changes procgen must not reuse, or mix with, older results.
    manifest = (SERVER / "steamapps/appmanifest_258550.acf").read_text()
    m = re.search(r'"buildid"\s+"(\d+)"', manifest)
    if not m:
        sys.exit("error: no buildid in server/steamapps/appmanifest_258550.acf")
    return OUT / m[1] / str(size)


# --- .map files (WorldSerialization): uint32 version 10, int64 timestamp, then an lz4net LZ4Stream of the
# --- ProtoBuf.WorldData message: 1 size, 2 maps (1 name, 2 data), 3 prefabs, 4 paths (1 name, 15 nodes).


def varint(buf: memoryview, i: int) -> tuple[int, int]:
    result = shift = 0
    while True:
        b = buf[i]
        i += 1
        result |= (b & 0x7F) << shift
        if b < 0x80:
            return result, i
        shift += 7


def fields(buf: memoryview) -> Iterator[tuple[int, Any]]:
    i = 0
    while i < len(buf):
        key, i = varint(buf, i)
        wire = key & 7
        if wire == 0:
            value, i = varint(buf, i)
        elif wire == 1:
            value, i = buf[i : i + 8], i + 8
        elif wire == 2:
            length, i = varint(buf, i)
            value, i = buf[i : i + length], i + length
        elif wire == 5:
            value, i = buf[i : i + 4], i + 4
        else:
            raise ValueError(f"unsupported protobuf wire type {wire}")
        yield key >> 3, value


def read_world(path: Path) -> memoryview:
    data = memoryview(path.read_bytes())
    (version,) = struct.unpack_from("<I", data)
    if version != 10:
        raise ValueError(f"{path}: WorldSerialization version {version}, expected 10")
    chunks, i = [], 12
    while i < len(data):
        flags, i = varint(data, i)
        length, i = varint(data, i)
        if flags >> 2:
            raise ValueError(f"{path}: multi-pass LZ4 chunk at byte {i}")
        if flags & 1:
            packed, i = varint(data, i)
            chunks.append(lz4.block.decompress(data[i : i + packed], uncompressed_size=length))
            i += packed
        else:
            chunks.append(bytes(data[i : i + length]))
            i += length
    return memoryview(b"".join(chunks))


def map_stats(path: Path) -> dict:
    layers, paths = {}, {}
    for num, value in fields(read_world(path)):
        if num == 2:
            parts = dict(fields(value))
            layers[bytes(parts[1]).decode()] = parts[2]
        elif num == 4:
            name, nodes = "", []
            for n, v in fields(value):
                if n == 1:
                    name = bytes(v).decode()
                elif n == 15:
                    xyz = {k: struct.unpack("<f", w)[0] for k, w in fields(v)}
                    nodes.append((round(xyz.get(1, 0.0), 1), round(xyz.get(3, 0.0), 1)))
            paths.setdefault(name.rstrip("0123456789 "), []).append(nodes)
    topology = np.frombuffer(layers["topology"], dtype="<i4")
    side = round(len(topology) ** 0.5)
    land = (topology.reshape(side, side) & OCEAN) == 0
    biome = np.frombuffer(layers["biome"], dtype=np.uint8)
    res = round((len(biome) / len(BIOMES)) ** 0.5)
    dominant = biome.reshape(len(BIOMES), res, res).argmax(axis=0)
    index = np.arange(res) * side // res
    land_at_biome = land[np.ix_(index, index)]
    on_land = dominant[land_at_biome]
    return {
        "land": round(float(land.mean()), 4),
        "biomes": {b: round(float((on_land == k).mean()), 4) for k, b in enumerate(BIOMES)},
        "paths": {kind: len(v) for kind, v in sorted(paths.items())},
        "rails": paths.get("Rail", []),
    }


# --- throwaway servers


async def rcon(port: int, password: str, command: str, timeout: float = 600) -> str:
    # Pings off: Rust's WebRCON may not answer while a long command holds the main thread.
    async with websockets.connect(f"ws://127.0.0.1:{port}/{password}", max_size=None, ping_interval=None) as ws:
        await ws.send(json.dumps({"Identifier": 1001, "Message": command, "Name": "WebRcon"}))
        async with asyncio.timeout(timeout):
            async for raw in ws:
                message = json.loads(raw)
                if message.get("Identifier") == 1001:
                    return message["Message"]
    raise RuntimeError(f"WebRCON closed before replying to {command!r}")


def parse_monuments(table: str) -> list[dict]:
    lines = [line for line in table.splitlines() if line.strip()]
    columns = ("type", "name", "prefab", "pos")
    starts = [lines[0].index(c) for c in columns]
    rows = []
    for line in lines[1:]:
        cells = [line[a:b].strip() for a, b in zip(starts, starts[1:] + [None])]
        x, y, z = (float(v) for v in cells[3].strip("()").split(","))
        rows.append({"type": cells[0], "name": cells[1], "prefab": cells[2], "x": x, "y": y, "z": z})
    return rows


async def wait_for_startup(proc: asyncio.subprocess.Process, log: Path, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            await asyncio.wait_for(proc.wait(), 5)
        except TimeoutError:
            if log.exists() and b"Server startup complete" in log.read_bytes():
                return
            continue
        raise RuntimeError(f"server exited with status {proc.returncode} during startup; see {log}")
    raise TimeoutError(f"no 'Server startup complete' within {timeout:.0f} s; see {log}")


async def stop(proc: asyncio.subprocess.Process) -> None:
    # The server has no signal handler, so SIGTERM ends it at once. Kill the whole group: steam-run's
    # bubblewrap children outlive a lone leader.
    for sig in (signal.SIGTERM, signal.SIGKILL):
        try:
            os.killpg(proc.pid, sig)
        except ProcessLookupError:
            return
        for _ in range(60):
            if proc.returncode is None:
                try:
                    await asyncio.wait_for(proc.wait(), 0.5)
                except TimeoutError:
                    continue
            try:
                os.killpg(proc.pid, 0)
            except ProcessLookupError:
                return
            await asyncio.sleep(0.5)
    raise RuntimeError(f"server process group {proc.pid} survived SIGKILL")


async def scan(slot: int, seed: int, size: int, scale: float, timeout: float, keep_map: bool) -> None:
    out = results_dir(size) / str(seed)
    out.mkdir(parents=True, exist_ok=True)
    identity = f"seedscan{slot}"
    identity_dir = SERVER / "server" / identity
    (identity_dir / "cfg").mkdir(parents=True, exist_ok=True)
    # Launch arguments cannot carry negative values (see CLAUDE.md); -1 keeps the Rust+ server off.
    (identity_dir / "cfg" / "server.cfg").write_text("app.port -1\n")
    port = BASE_PORT + 10 * slot
    for _ in range(60):
        if ports_free(port):
            break
        await asyncio.sleep(2)
    else:
        raise RuntimeError(f"ports {port} (UDP) and {port + 1} (TCP) stayed in use")
    password = secrets.token_hex(16)
    log = out / "server.log"
    plugins = SERVER / "RustDedicated_Data/Plugins"
    env = dict(os.environ, HOME=str(REPO / "home"))
    env["LD_LIBRARY_PATH"] = ":".join(filter(None, (str(plugins), str(plugins / "x86_64"), env.get("LD_LIBRARY_PATH"))))
    argv = [
        "steam-run", "./RustDedicated", "-batchmode", "-insecure",
        "+server.identity", identity, "+server.hostname", f"Seed scan {slot}", "+server.port", str(port),
        "+server.level", "Procedural Map", "+server.seed", str(seed), "+server.worldsize", str(size),
        "+server.maxplayers", "1", "+server.encryption", "0", "+server.saveinterval", "86400",
        "+server.autouploadmap", "0", "+aimanager.nav_disable", "1",
        "+rcon.ip", "127.0.0.1", "+rcon.port", str(port + 1), "+rcon.web", "1", "+rcon.password", password,
        "-logfile", str(log),
    ]  # fmt: skip
    print(f"{seed}: generating on {identity}", flush=True)
    started = time.monotonic()
    with open(out / "stdout.log", "wb") as stdout:  # noqa: ASYNC230
        proc = await asyncio.create_subprocess_exec(
            *argv, cwd=SERVER, env=env, stdin=asyncio.subprocess.DEVNULL, stdout=stdout,
            stderr=asyncio.subprocess.STDOUT, start_new_session=True,
        )  # fmt: skip
    try:
        await wait_for_startup(proc, log, timeout)
        boot = time.monotonic() - started
        monuments = parse_monuments(await rcon(port + 1, password, "world.monuments"))
        reply = await rcon(port + 1, password, f"world.rendermap {scale}")
        m = re.fullmatch(r"Saved map render to: (.+)", reply.strip())
        if not m:
            raise RuntimeError(f"world.rendermap: {reply.strip()}")
        shutil.move(m[1], out / "map.png")
    finally:
        await stop(proc)
    world_files = sorted(identity_dir.glob(f"proceduralmap.{size}.{seed}.*"))
    for f in world_files:
        if f.suffix == ".map":
            shutil.move(f, out / "world.map")
        else:
            f.unlink()
    timings = {m[2]: float(m[1]) for m in re.finditer(r"^\[([0-9.]+)s\] (.+)$", log.read_text(), re.MULTILINE)}
    (out / "monuments.json").write_text(json.dumps(monuments, indent=1) + "\n")
    stats = map_stats(out / "world.map") | {"boot_seconds": round(boot), "timings": timings}
    (out / "stats.json").write_text(json.dumps(stats) + "\n")
    if not keep_map:
        (out / "world.map").unlink()
    print(f"{seed}: done in {boot:.0f} s, {sum(1 for r in monuments if r['name'])} named monuments", flush=True)


def ports_free(port: int) -> bool:
    # A plain bind also fails while the last server's RCON connection sits in TIME_WAIT.
    for kind, address in ((socket.SOCK_DGRAM, ("0.0.0.0", port)), (socket.SOCK_STREAM, ("127.0.0.1", port + 1))):
        with socket.socket(socket.AF_INET, kind) as s:
            try:
                s.bind(address)
            except OSError:
                return False
    return True


def claim_slot() -> int:
    # The flock keeps concurrent runs on distinct identities and ports until this process exits.
    for slot in itertools.count(1):
        identity_dir = SERVER / "server" / f"seedscan{slot}"
        identity_dir.mkdir(parents=True, exist_ok=True)
        fd = os.open(identity_dir / ".lock", os.O_RDWR | os.O_CREAT, 0o644)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            os.close(fd)
            continue
        if ports_free(BASE_PORT + 10 * slot):
            return slot
        os.close(fd)
    raise AssertionError("unreachable")


async def scan_all(seeds: list[int], size: int, jobs: int, scale: float, timeout: float, keep_map: bool) -> int:
    queue: asyncio.Queue[int] = asyncio.Queue()
    for seed in seeds:
        if (results_dir(size) / str(seed) / "monuments.json").exists():
            print(f"{seed}: already scanned", flush=True)
        else:
            queue.put_nowait(seed)
    failed = []

    async def worker(slot: int) -> None:
        while not queue.empty():
            seed = queue.get_nowait()
            try:
                await scan(slot, seed, size, scale, timeout, keep_map)
            except (OSError, RuntimeError, ValueError, KeyError, websockets.WebSocketException) as e:
                print(f"{seed}: FAILED: {e!r}", file=sys.stderr, flush=True)
                failed.append(seed)

    await asyncio.gather(*(worker(claim_slot()) for _ in range(min(jobs, queue.qsize()))))
    if failed:
        print(f"failed seeds: {' '.join(map(str, failed))}", file=sys.stderr)
    return len(failed)


# --- report


def display_name(row: dict) -> str:
    # Variants of one monument differ in case ("Jungle Ruin", "Jungle ruin") or by a letter ("Canyon A", "Oasis C").
    name = " ".join(w[:1].upper() + w[1:] for w in row["name"].split())
    return re.sub(r" [A-Z]$", "", name)


def grid_label(column: int) -> str:
    text, n = "", column + 1
    while n > 0:
        n -= 1
        text = chr(65 + n % 26) + text
        n //= 26
    return text


def label_map(out: Path, size: int, monuments: list[dict], rails: list) -> None:
    img = Image.open(out / "map.png").convert("RGB")
    w, h = img.size
    res = w - 2 * MARGIN

    def px(x: float, z: float) -> tuple[float, float]:
        return MARGIN + (x / size + 0.5) * res, h - 1 - (MARGIN + (z / size + 0.5) * res)

    draw = ImageDraw.Draw(img, "RGBA")
    font = ImageFont.load_default(size=max(14, res // 90))
    cells = int(size / GRID + 0.001)
    cell = size // cells
    for c in range(cells + 1):
        x, _ = px(-size / 2 + c * cell, 0)
        _, y = px(0, size / 2 - c * cell)
        draw.line([(x, MARGIN), (x, h - MARGIN)], fill=(0, 0, 0, 50))
        draw.line([(MARGIN, y), (w - MARGIN, y)], fill=(0, 0, 0, 50))
        if c < cells:
            draw.text((x + cell * res / size / 2, MARGIN - 8), grid_label(c), font=font, anchor="md", fill="white")
            draw.text((MARGIN - 8, y + cell * res / size / 2), str(c), font=font, anchor="rm", fill="white")
    for nodes in rails:
        draw.line([px(x, z) for x, z in nodes], fill=(40, 40, 40, 200), width=3)
    for row in monuments:
        name = display_name(row)
        if not name or name in MINOR:
            continue
        x, y = px(row["x"], row["z"])
        draw.ellipse([x - 5, y - 5, x + 5, y + 5], fill=(255, 60, 60), outline="black")
        draw.text((x + 8, y), name, font=font, anchor="lm", fill="white", stroke_width=3, stroke_fill="black")
    img.save(out / "map-labeled.jpg", quality=85)


def report(size: int) -> Path:
    base = results_dir(size)
    script_mtime = Path(__file__).stat().st_mtime
    seeds = []
    large: set[str] = set()
    for d in sorted(base.iterdir(), key=lambda p: int(p.name) if p.name.isdigit() else -1):
        if not (d / "monuments.json").exists():
            continue
        if not (d / "stats.json").exists() and (d / "world.map").exists():
            (d / "stats.json").write_text(json.dumps(map_stats(d / "world.map")) + "\n")
        monuments = json.loads((d / "monuments.json").read_text())
        stats = json.loads((d / "stats.json").read_text())
        labeled = d / "map-labeled.jpg"
        if not labeled.exists() or labeled.stat().st_mtime < max((d / "map.png").stat().st_mtime, script_mtime):
            label_map(d, size, monuments, stats.get("rails", []))
        counts: dict[str, int] = {}
        minor: dict[str, int] = {}
        for row in monuments:
            if name := display_name(row):
                tally = minor if name in MINOR else counts
                tally[name] = tally.get(name, 0) + 1
                # Some rows (Train Tunnel Link) carry a bare GameObject name instead of a prefab path.
                if Path(row["prefab"]).parent.name in ("xlarge", "large"):
                    large.add(name)
        seeds.append({"seed": int(d.name), "counts": counts, "minor": minor, "stats": stats})
    if not seeds:
        sys.exit(f"error: nothing scanned under {base}")
    names: dict[str, int] = {}
    for s in seeds:
        for name in s["counts"]:
            names[name] = names.get(name, 0) + 1
    # A quarter, not half: the large monuments that compete for space on small maps are the ones worth flagging.
    common = sorted((n for n, k in names.items() if k * 4 >= len(seeds)), key=lambda n: (-names[n], n))
    for s in seeds:
        s["missing"] = [n for n in common if n not in s["counts"]]
    seeds.sort(key=lambda s: (len(s["missing"]), -s["stats"]["land"]))
    current = re.search(r"\+server\.seed (\d+)", (REPO / "start.sh").read_text())
    live_seed = int(current[1]) if current else None

    def names_html(items: list[str]) -> str:
        return ", ".join(f"<b>{html.escape(n)}</b>" if n in large else html.escape(n) for n in items) or "none"

    rows, cards = [], []
    for s in seeds:
        seed, counts, minor, stats, missing = s["seed"], s["counts"], s["minor"], s["stats"], s["missing"]
        rare = sorted(n for n in counts if n not in common)
        biomes = stats["biomes"]
        paths = stats["paths"]
        tag = " (current)" if seed == live_seed else ""
        caves = minor.get("Underground Cave", 0)
        cells = [
            (f'<a href="#s{seed}">{seed}</a>{tag}', seed),
            (f"{stats['land']:.0%}", stats["land"]),
            *((f"{biomes[b]:.0%}", biomes[b]) for b in BIOMES),
            (paths.get("River", 0), paths.get("River", 0)),
            (paths.get("Rail", 0), paths.get("Rail", 0)),
            (caves, caves),
            (len(counts), len(counts)),
            (sum(counts.values()), sum(counts.values())),
            (names_html(missing), len(missing)),
            (names_html(rare), len(rare)),
        ]
        rows.append("<tr>" + "".join(f'<td data-v="{v}">{t}</td>' for t, v in cells) + "</tr>")

        def listing(tally: dict[str, int]) -> str:
            return ", ".join(f"{html.escape(n)}{f' x{k}' if k > 1 else ''}" for n, k in sorted(tally.items()))

        cards.append(
            f'<section id="s{seed}"><h2>{seed}{tag}</h2>'
            f'<p><a href="{seed}/map.png">full render</a> · <a href="https://rustmaps.com/map/{size}_{seed}">'
            f"rustmaps.com</a> · boot {stats.get('boot_seconds', '?')} s</p>"
            f"<p><b>Missing:</b> {names_html(missing)}<br><b>Monuments:</b> {listing(counts)}"
            f"<br><b>Minor:</b> {listing(minor)}</p>"
            f'<a href="{seed}/map-labeled.jpg"><img src="{seed}/map-labeled.jpg" loading="lazy"></a></section>'
        )
    heads = ["seed", "land", *BIOMES, "rivers", "rails", "caves", "types", "monuments", "missing", "rare"]
    page = f"""<!doctype html><meta charset="utf-8"><title>Seed scan {size}</title>
<style>body{{font:14px sans-serif;margin:1em}}table{{border-collapse:collapse}}td,th{{border:1px solid #ccc;
padding:2px 6px;vertical-align:top}}th{{cursor:pointer;background:#eee}}td:nth-last-child(-n+2){{max-width:28em}}
img{{width:100%;max-width:1400px}}</style>
<h1>Procedural map seeds, worldsize {size}, buildid {base.parent.name}</h1>
<p>{len(seeds)} seeds, fewest missing monuments first, then most land. Land: share of the {size} m square that is
not ocean. Biomes: dominant biome per pixel, as a share of the land. Rails: above-ground railway paths, which need
worldsize 4250 or more. Types and monuments count named monuments other than {", ".join(MINOR).lower()}. Missing:
monuments that at least a quarter of the scanned seeds have; bold ones come from the xlarge and large prefab
folders, which compete for space on small maps. Rare: the rest. Click a column to sort.</p>
<table><thead><tr>{"".join(f"<th>{h}</th>" for h in heads)}</tr></thead><tbody>{"".join(rows)}</tbody></table>
{"".join(cards)}
<script>document.querySelectorAll("th").forEach((th,i)=>th.onclick=()=>{{const b=th.closest("table").tBodies[0];
const d=th.dataset.d=th.dataset.d==="1"?"-1":"1";[...b.rows].sort((x,y)=>{{const p=x.cells[i].dataset.v,
q=y.cells[i].dataset.v;return(isNaN(p)||isNaN(q)?p.localeCompare(q):p-q)*d}}).forEach(r=>b.append(r))}})</script>
"""
    index = base / "index.html"
    index.write_text(page)
    return index


def main() -> None:
    parser = argparse.ArgumentParser(description=(__doc__ or "").split("\n\n")[0])
    parser.add_argument("seeds", nargs="*", type=int, help="seeds to scan (1 to 2147483647)")
    parser.add_argument("--size", type=int, default=None, help="worldsize (default: start.sh's)")
    parser.add_argument("--random", type=int, default=0, metavar="N", help="also scan N random seeds")
    parser.add_argument("--jobs", type=int, default=2, help="servers at once (default 2)")
    parser.add_argument("--scale", type=float, default=0.5, help="world.rendermap scale (default 0.5)")
    parser.add_argument("--timeout", type=float, default=1800, help="seconds to wait for startup (default 1800)")
    parser.add_argument("--keep-map", action="store_true", help="keep each generated .map as world.map")
    parser.add_argument("--report", action="store_true", help="only rebuild index.html")
    parser.add_argument("--stats", type=Path, metavar="FILE", help="print the stats of a .map file and exit")
    args = parser.parse_args()
    if args.stats:
        stats = map_stats(args.stats)
        stats["rails"] = f"{len(stats['rails'])} paths"
        print(json.dumps(stats, indent=1))
        return
    size = args.size or default_size()
    seeds = list(dict.fromkeys(args.seeds + [secrets.randbelow(2**31 - 1) + 1 for _ in range(args.random)]))
    if any(not 0 < s < 2**31 for s in seeds):
        sys.exit("error: server.seed is an int; launch arguments cannot carry negative values")
    failed = 0
    if seeds and not args.report:
        try:
            failed = asyncio.run(scan_all(seeds, size, args.jobs, args.scale, args.timeout, args.keep_map))
        except KeyboardInterrupt:
            sys.exit("interrupted; running servers were stopped, finished seeds are kept")
    print(f"report: {report(size)}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
