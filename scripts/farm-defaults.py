#!/usr/bin/env python3
"""Write LivestockFarm.cs's built-in defaults from the shipped config, and check them against it.

usage (from the repository root):
  scripts/farm-defaults.py CONFIG PLUGIN [--out FILE]
  scripts/farm-defaults.py CONFIG PLUGIN --check
  scripts/farm-defaults.py CONFIG PLUGIN --verify
  scripts/farm-defaults.py CONFIG --compare WRITTEN

The first form rewrites in PLUGIN (or writes to --out):
- DefaultLayout() and the comment above it: one Entry call per layout entry carrying its v2 fields, and one Link call
  per connection with its Route. Each route point is written through the first WirePoint helper of the plugin
  (BarnWire, TowerWire, GroundWire, ...) that reproduces it exactly in float32, else as a `new WirePoint { ... }`.
- the Entry helper and DefaultSlotPlan().
- the initializer of every scalar setting and every List<string> setting whose value in CONFIG differs. A rewritten
  list names a `private const string` where the old initializer used one for the same value.
The comment's timestamp is CONFIG's mtime, kept as it is when nothing else changes. Afterwards it compiles the result
as --verify does and exits 2 naming every setting it cannot write (tables such as the starter herd or swept items)
that still differs; edit those by hand. --no-verify skips that step.

--check exits 1 when PLUGIN differs from what the first form would write, without Mono.
--verify compiles PLUGIN's constants and its Configuration region (Configuration through the WirePoint helpers) with
Mono (nixpkgs#mono) against the game's Newtonsoft.Json.dll (MANAGED_DIR, default server/RustDedicated_Data/Managed),
serializes `new Configuration()`, which LoadDefaultConfig creates and SaveConfig writes, and compares the result with
CONFIG as --compare does.
--compare compares a config the plugin wrote with CONFIG, ignoring key order and the v2 fields the plugin leaves out
at their defaults (Y, Pitch, and Roll 0, Mount false). Both exit 1 on a difference.
"""

import argparse
import datetime
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from decimal import Decimal
from pathlib import Path

WIDTH = 120
INDENT = " " * 12
OPTIONAL = [  # Entry parameter, JSON key, value the plugin treats as absent
    ("on", "On", None),
    ("y", "Y", 0.0),
    ("building", "Building", None),
    ("parent", "Parent", None),
    ("mount", "Mount", False),
    ("pitch", "Pitch", 0.0),
    ("roll", "Roll", 0.0),
    ("grade", "Grade", None),
    ("skin", "Skin", None),
    ("layer", "Layer", None),
    ("image", "Image", None),
    ("replaces", "Replaces", None),
]
ENTRY_KEYS = {"Id", "Role", "Prefab", "X", "Z", "Yaw"} | {key for _, key, _ in OPTIONAL}
LINK_KEYS = {"From", "FromSlot", "To", "ToSlot", "Kind", "Route"}
POINT_KEYS = {"Anchor", "X", "Y", "Z"}
OMITTED_AT_DEFAULT = {"Y": 0.0, "Pitch": 0.0, "Roll": 0.0, "Mount": False}
CSHARP_KEYWORDS = {
    "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
    "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
    "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
    "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
    "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
    "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
    "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
}
SLOT_PLAN = "Planter slot plan (layout id to clone shortname per slot)"
STAMP = re.compile(r"^        // (\S+) as of (\d{4}-\d\d-\d\d \d\d:\d\d): ", re.M)
ENTRY_HELPER = """\
        private static LayoutEntry Entry(string id, string role, string prefab, float x, float z, float yaw,
            string on = null, float y = 0f, string building = null, string parent = null, bool mount = false,
            float pitch = 0f, float roll = 0f, string grade = null, ulong? skin = null, int? layer = null,
            string image = null, string replaces = null) =>
            new LayoutEntry
            {
                Id = id, Role = role, Prefab = prefab, X = x, Z = z, Yaw = yaw, On = on, Y = y, Building = building,
                Parent = parent, Mount = mount, Pitch = pitch, Roll = roll, Grade = grade, Skin = skin, Layer = layer,
                Image = image, Replaces = replaces,
            };
"""
HARNESS = """\
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Oxide.Plugins
{{
    public class LivestockFarmDefaults
    {{
{consts}

{region}
        public static void Main(string[] args) =>
            File.WriteAllText(args[0], JsonConvert.SerializeObject(new Configuration(), Formatting.Indented));
    }}
}}
"""


def f32(value: float) -> float:
    return struct.unpack("<f", struct.pack("<f", value))[0]


def f32_neighbours(value: float, count: int) -> list[float]:
    """The float32 values within count steps of value's float32, nearest first."""
    bits = struct.unpack("<i", struct.pack("<f", value))[0]
    found = []
    for step in range(count + 1):
        for b in {bits + step, bits - step}:
            found.append(struct.unpack("<f", struct.pack("<i", b))[0])
    return found


def float_text(value: float) -> str:
    """The shortest decimal, without an exponent, that parses to the same float32, as C# needs for a literal."""
    target = f32(value)
    text = repr(target)
    for digits in range(1, 10):
        candidate = f"{target:.{digits}g}"
        if f32(float(candidate)) == target:
            text = candidate
            break
    text = format(Decimal(text), "f")
    return text if "." in text else text + ".0"


def literal(value) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        return float_text(value) + "f"
    return json.dumps(value)


def ident(stem: str) -> str:
    parts = [p for p in re.split(r"[^A-Za-z0-9]+", stem) if p]
    name = parts[0].lower() + "".join(p[:1].upper() + p[1:] for p in parts[1:])
    name = name if not name[0].isdigit() else "p" + name
    return name + "Prefab" if name in CSHARP_KEYWORDS else name


def wrap_words(prefix: str, text: str, cont: str | None = None) -> list[str]:
    lines, line, lead = [], prefix, prefix
    for word in text.split():
        if line != lead and len(line) + 1 + len(word) > WIDTH:
            lines.append(line)
            lead = line = cont if cont is not None else prefix
        line += ("" if line == lead else " ") + word
    lines.append(line)
    return lines


def wrap_call(head: str, args: list[str]) -> list[str]:
    """`head(arg, ...));` on one line, or broken after a comma before the 120th column."""
    one = f"{INDENT}{head}({', '.join(args)}));"
    if len(one) <= WIDTH:
        return [one]
    lines, line = [], f"{INDENT}{head}("
    for i, arg in enumerate(args):
        piece = arg + (", " if i < len(args) - 1 else "));")
        if len(line) + len(piece.rstrip()) > WIDTH:
            lines.append(line.rstrip())
            line = INDENT + "    "
        line += piece
    lines.append(line)
    return lines


def string_const(name: str, value: str) -> list[str]:
    one = f'{INDENT}const string {name} = "{value}";'
    if len(one) <= WIDTH:
        return [one]
    lines, rest = [f"{INDENT}const string {name} ="], value
    cont = INDENT + "    "
    while len(cont) + len(rest) + 3 > WIDTH:
        cut = rest.rfind("/", 0, WIDTH - len(cont) - 5) + 1
        if cut <= 0:
            break
        lines.append(f'{cont}"{rest[:cut]}" +')
        rest = rest[cut:]
    lines.append(f'{cont}"{rest}";')
    return lines


def string_constants(source: str) -> dict[str, str]:
    """Name -> value of the plugin's single-literal `private const string`s."""
    return dict(re.findall(r'^        private const string (\w+) =\s*"([^"]*)";', source, re.M))


def prefab_constants(source: str) -> dict[str, str]:
    return {path: name for name, path in string_constants(source).items() if name.endswith("Prefab")}


class WireHelper:
    """A `private static WirePoint Name(float a, ...) => new WirePoint { Field = expr, ... };` of the plugin, where
    each expr is a parameter, a parameter plus or minus a float literal, a float literal, or the Anchor string."""

    PATTERN = re.compile(r"^        private static WirePoint (\w+)\(([^)]*)\) =>\s*new WirePoint \{ ([^}]*) \};", re.M)

    def __init__(self, name: str, params: str, body: str):
        self.name = name
        self.params = [p.split()[1] for p in params.split(",") if p.strip()]
        self.anchor = None
        self.fields: dict[str, tuple] = {}  # field -> ("param", name, offset) or ("const", value)
        for assignment in body.split(","):
            field, expr = (s.strip() for s in assignment.split("=", 1))
            if field == "Anchor":
                self.anchor = json.loads(expr)
                continue
            m = re.fullmatch(r"(\w+)(?: ([+-]) ([0-9.]+)f)?", expr)
            if m and m.group(1) in self.params:
                offset = Decimal(m.group(3) or "0") * (-1 if m.group(2) == "-" else 1)
                self.fields[field] = ("param", m.group(1), offset)
            elif re.fullmatch(r"-?[0-9.]+f", expr):
                self.fields[field] = ("const", float(expr[:-1]))
            else:
                raise ValueError(f"{name}: cannot invert {field} = {expr}")
        if set(self.fields) != {"X", "Y", "Z"}:
            raise ValueError(f"{name}: sets {sorted(self.fields)}, not X, Y, and Z")

    def call(self, point: dict) -> str | None:
        if point.get("Anchor") != self.anchor:
            return None
        args = {}
        for field, spec in self.fields.items():
            value = f32(float(point[field]))
            if spec[0] == "const":
                if f32(spec[1]) != value:
                    return None
                continue
            _, param, offset = spec
            # The plugin computes the field in float32 and the config holds that result, so look for the shortest
            # argument among the float32 values near the exact inverse.
            fits = [float_text(a) for a in f32_neighbours(value - f32(float(offset)), 4)
                    if f32(f32(a) + f32(float(offset))) == value]
            if not fits:
                return None
            text = min(fits, key=len)
            if args.get(param, text) != text:
                return None
            args[param] = text
        return f"{self.name}({', '.join(args[p] + 'f' for p in self.params)})"


def wire_helpers(source: str) -> list[WireHelper]:
    helpers = []
    for m in WireHelper.PATTERN.finditer(source):
        try:
            helpers.append(WireHelper(*m.groups()))
        except ValueError as e:
            print(f"skipping wire helper: {e}", file=sys.stderr)
    return helpers


def wire_point(point: dict, helpers: list[WireHelper]) -> str:
    unknown = set(point) - POINT_KEYS
    if unknown:
        raise SystemExit(f"route point {point}: no WirePoint field for {', '.join(sorted(unknown))}")
    for helper in helpers:
        call = helper.call(point)
        if call:
            return call
    fields = ([f"Anchor = {json.dumps(point['Anchor'])}"] if point.get("Anchor") is not None else []) + \
        [f"{k} = {literal(float(point.get(k, 0.0)))}" for k in ("X", "Y", "Z")]
    return f"new WirePoint {{ {', '.join(fields)} }}"


def argument(key: str, value) -> str:
    if key == "Skin":
        return f"{int(value)}UL"
    if key == "Layer":
        return str(int(value))
    if key in ("Y", "Pitch", "Roll"):
        return literal(float(value))
    return literal(value)


def layout_method(config: dict, source: str, source_name: str, stamp: str) -> str:
    constants, helpers = prefab_constants(source), wire_helpers(source)
    layout = config["Layout"]
    entries, links = layout["Entries"], layout["Connections"]
    locals_: dict[str, str] = {}
    used = set(constants.values())
    for entry in entries:
        path = entry["Prefab"]
        if path in constants or path in locals_:
            continue
        base = name = ident(Path(path).name.removesuffix(".prefab"))
        n = 2
        while name in used:
            name, n = f"{base}{n}", n + 1
        used.add(name)
        locals_[path] = name
    buildings = sorted({e["Building"] for e in entries if e.get("Building")})
    comment = (
        f"{source_name} as of {stamp}: {len(entries)} entries (buildings {', '.join(buildings)}) and "
        f"{len(links)} connections. scripts/farm-defaults.py writes this method, "
        "DefaultSlotPlan, and the setting defaults from that file; edit the file and rerun it."
    )
    out = wrap_words("        // ", comment)
    out += ["        private static FarmLayout DefaultLayout()", "        {"]
    for path, name in locals_.items():
        out += string_const(name, path)
    centre = layout["Centre"]
    out.append(f"{INDENT}var layout = new FarmLayout {{ Centre = new XZ {{ X = {literal(float(centre['x']))}, "
               f"Z = {literal(float(centre['z']))} }}, Yaw = {literal(float(layout['Yaw']))} }};")
    out.append(f"{INDENT}List<LayoutEntry> e = layout.Entries;")
    for entry in entries:
        unknown = set(entry) - ENTRY_KEYS
        if unknown:
            raise SystemExit(f"{entry['Id']}: no Entry parameter for {', '.join(sorted(unknown))}")
        args = [json.dumps(entry["Id"]), json.dumps(entry["Role"]),
                constants.get(entry["Prefab"]) or locals_[entry["Prefab"]],
                literal(float(entry["X"])), literal(float(entry["Z"])), literal(float(entry["Yaw"]))]
        for param, key, absent in OPTIONAL:
            value = entry.get(key, absent)
            if value is not None and value != absent:
                args.append(f"{param}: {argument(key, value)}")
        out += wrap_call("e.Add(Entry", args)
    out.append(f"{INDENT}List<LayoutConnection> c = layout.Connections;")
    for link in links:
        unknown = set(link) - LINK_KEYS
        if unknown:
            raise SystemExit(f"link {link}: no Link parameter for {', '.join(sorted(unknown))}")
        route = link.get("Route")
        if route is not None and not route:
            raise SystemExit(f"link {link}: an empty Route reads back as none; drop it")
        args = [json.dumps(link["From"]), str(link["FromSlot"]), json.dumps(link["To"]), str(link["ToSlot"]),
                json.dumps(link["Kind"])]
        out += wrap_call("c.Add(Link", args + [wire_point(p, helpers) for p in route or []])
    out += [f"{INDENT}return layout;", "        }"]
    return "\n".join(out) + "\n"


def slot_plan_method(plan: dict) -> str:
    lines = ["        private static Dictionary<string, List<string>> DefaultSlotPlan() => "
             "new Dictionary<string, List<string>>", "        {"]
    for planter, clones in plan.items():
        if len(set(clones)) == 1:
            lines.append(f'            ["{planter}"] = Enumerable.Repeat("{clones[0]}", {len(clones)}).ToList(),')
            continue
        line = f'            ["{planter}"] = new List<string> {{ {", ".join(json.dumps(c) for c in clones)} }},'
        if len(line) <= WIDTH:
            lines.append(line)
            continue
        lines += [f'            ["{planter}"] = new List<string>', "            {"]
        lines += wrap_words("                ", " ".join(json.dumps(c) + "," for c in clones))
        lines.append("            },")
    lines.append("        };")
    return "\n".join(lines) + "\n"


def member_span(source: str, start_pattern: str, with_comment: bool) -> tuple[int, int]:
    """From the member's first line (or its leading comment) through the line that closes it."""
    m = re.search(start_pattern, source, re.M)
    if not m:
        raise SystemExit(f"cannot find {start_pattern!r} in the plugin")
    start = source.rfind("\n", 0, m.start()) + 1
    while with_comment:
        prev = source.rfind("\n", 0, start - 1) + 1
        if not source[prev:start].lstrip().startswith("//"):
            break
        start = prev
    depth = 0
    for i in range(m.end(), len(source)):
        ch = source[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return start, source.find("\n", i) + 1
        elif ch == ";" and depth == 0:
            return start, source.find("\n", i) + 1
    raise SystemExit(f"unterminated member at {start_pattern!r}")


SETTING = re.compile(r'\[JsonProperty\("([^"]+)"[^\]]*\)\]\s*\n\s*public (int|float|bool|string|ulong|List<string>) '
                     r'\w+ =\s*([^;]+);')


def setting_fields(source: str) -> dict[str, tuple[str, str, int, int]]:
    """JSON key -> (C# type, initializer, start, end of the initializer) for the scalar and string-list settings."""
    return {m.group(1): (m.group(2), m.group(3), m.start(3), m.end(3)) for m in SETTING.finditer(source)}


def scalar_literal(kind: str, value) -> str:
    if kind == "float":
        return literal(float(value))
    if kind == "string":
        return json.dumps(value)
    if kind == "bool":
        return "true" if value else "false"
    return str(int(value))


def list_value(init: str, consts: dict[str, str]) -> tuple[list[str] | None, dict[str, str]]:
    """The strings a `new List<string> { ... }` initializer holds, or None when it is anything else, and the consts
    it names by value."""
    m = re.fullmatch(r"new List<string>\s*(?:\(\s*\))?\s*(?:\{(.*)\})?", init.strip(), re.S)
    if not m:
        return None, {}
    values, named = [], {}
    for token in re.findall(r'"(?:[^"\\]|\\.)*"|[^,\s]+', m.group(1) or ""):
        if token.startswith('"'):
            values.append(json.loads(token))
        elif token in consts:
            values.append(consts[token])
            named[consts[token]] = token
        else:
            return None, {}
    return values, named


def list_literal(values: list[str], named: dict[str, str], column: int) -> str:
    items = [named.get(v) or json.dumps(v) for v in values]
    one = "new List<string>()" if not items else f"new List<string> {{ {', '.join(items)} }}"
    if column + len(one) + 1 <= WIDTH:
        return one
    body = wrap_words(INDENT + "    ", " ".join(item + "," for item in items))
    return "\n".join(["new List<string>", INDENT + "{"] + body + [INDENT + "}"])


def splice(config: dict, source: str, source_name: str, stamp: str) -> str:
    start, end = member_span(source, r"^        private static FarmLayout DefaultLayout\(\)", True)
    source = source[:start] + layout_method(config, source, source_name, stamp) + source[end:]
    start, end = member_span(source, r"^        private static LayoutEntry Entry\(", False)
    source = source[:start] + ENTRY_HELPER + source[end:]
    start, end = member_span(source, r"^        private static Dictionary<string, List<string>> DefaultSlotPlan\(\)",
                             False)
    source = source[:start] + slot_plan_method(config[SLOT_PLAN]) + source[end:]
    consts = string_constants(source)
    for key, (kind, init, a, b) in sorted(setting_fields(source).items(), key=lambda kv: -kv[1][2]):
        if key not in config:
            continue
        if kind == "List<string>":
            current, named = list_value(init, consts)
            if current is None or current == config[key]:
                continue  # an initializer it cannot read is left to the verify step
            column = a - source.rfind("\n", 0, a) - 1
            source = source[:a] + list_literal(config[key], named, column) + source[b:]
            continue
        if kind == "float" and re.fullmatch(r"-?[0-9.]+f", init) and f32(float(init[:-1])) == f32(config[key]):
            continue
        if init.strip() != scalar_literal(kind, config[key]):
            source = source[:a] + scalar_literal(kind, config[key]) + source[b:]
    return source


def generate(config: dict, source: str, source_name: str, mtime_stamp: str) -> str:
    current = STAMP.search(source)
    if current:
        result = splice(config, source, source_name, current.group(2))
        if result == source:
            return source
    return splice(config, source, source_name, mtime_stamp)


def normalized(config: dict):
    data = json.loads(json.dumps(config), parse_float=lambda s: round(float(s), 5))
    for entry in data.get("Layout", {}).get("Entries", []):
        for key, default in OMITTED_AT_DEFAULT.items():
            if key in entry and entry[key] == default and isinstance(entry[key], bool) == isinstance(default, bool):
                del entry[key]
    return data


def compare(config: dict, written: dict, written_name: str, config_name: str) -> int:
    a, b = normalized(config), normalized(written)
    if a == b:
        print(f"{written_name} equals {config_name} (key order and default-valued v2 fields aside)")
        return 0
    for key in sorted(set(a) | set(b)):
        if key != "Layout" and a.get(key) != b.get(key):
            print(f"differs: {key}: {json.dumps(a.get(key))[:200]} vs {json.dumps(b.get(key))[:200]}")
    la, lb = a.get("Layout", {}), b.get("Layout", {})
    for key in ("Centre", "Yaw"):
        if la.get(key) != lb.get(key):
            print(f"differs: Layout.{key}: {la.get(key)} vs {lb.get(key)}")
    for key in ("Entries", "Connections"):
        xa, xb = la.get(key, []), lb.get(key, [])
        if len(xa) != len(xb):
            print(f"differs: Layout.{key}: {len(xa)} vs {len(xb)} items")
        for x, y in zip(xa, xb):
            if x != y:
                print(f"differs: first differing Layout.{key} item: {json.dumps(x)} vs {json.dumps(y)}")
                break
    return 1


def serialize(source: str) -> dict:
    """`new Configuration()` of the plugin source, serialized by the game's Newtonsoft.Json under Mono."""
    # Literal constants only: the Configuration region names prefabs, roles, and species, never game types.
    consts = [c.rstrip() for c in re.findall(r"^        private const \w+ \w+ =.*?;\n", source, re.M | re.S)
              if re.match(r'\s*("|-?[0-9])', c.split("=", 1)[1])]
    region_start = re.search(r"^        private class Configuration$", source, re.M)
    region_end = re.search(r"^        protected override void LoadDefaultConfig\(\)", source, re.M)
    if not consts or not region_start or not region_end:
        raise SystemExit("cannot find the constants and the Configuration region in the plugin")
    harness = HARNESS.format(consts="\n".join(consts), region=source[region_start.start():region_end.start()])
    managed = Path(os.environ.get("MANAGED_DIR", "server/RustDedicated_Data/Managed"))
    with tempfile.TemporaryDirectory(prefix="farm-defaults-") as tmp:
        work = Path(tmp)
        (work / "Defaults.cs").write_text(harness)
        shutil.copy(managed / "Newtonsoft.Json.dll", work)
        # The game's Newtonsoft.Json targets netstandard 2.0, which Mono provides as a facade next to its mscorlib.
        script = ('m=$(dirname "$(dirname "$(readlink -f "$(command -v mono)")")") && '
                  'mcs -target:exe -r:"$m/lib/mono/4.5/Facades/netstandard.dll" -r:"$1/Newtonsoft.Json.dll" '
                  '-out:"$1/defaults.exe" "$1/Defaults.cs" && mono "$1/defaults.exe" "$1/written.json"')
        subprocess.run(["nix", "shell", "nixpkgs#mono", "-c", "sh", "-c", script, "sh", tmp], check=True)
        return json.loads((work / "written.json").read_text())


def display_path(path: Path) -> str:
    resolved = path.resolve()
    for parent in resolved.parents:
        if (parent / ".git").exists():
            return str(resolved.relative_to(parent))
    return str(path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("config")
    parser.add_argument("plugin", nargs="?")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--out")
    mode.add_argument("--check", action="store_true")
    mode.add_argument("--verify", action="store_true")
    mode.add_argument("--compare", metavar="WRITTEN")
    parser.add_argument("--no-verify", action="store_true", help="skip the Mono check after writing")
    args = parser.parse_args()
    config_path = Path(args.config)
    config = json.loads(config_path.read_text())
    if args.compare:
        return compare(config, json.loads(Path(args.compare).read_text()), args.compare, args.config)
    if not args.plugin:
        parser.error("PLUGIN is required unless --compare is given")
    plugin = Path(args.plugin)
    source = plugin.read_text()
    if args.verify:
        return compare(config, serialize(source), "new Configuration() serialized", args.config)
    stamp = datetime.datetime.fromtimestamp(config_path.stat().st_mtime).strftime("%Y-%m-%d %H:%M")
    result = generate(config, source, display_path(config_path), stamp)
    if args.check:
        if result == source:
            print(f"{plugin}: defaults match {args.config}")
            return 0
        print(f"{plugin}: defaults differ from {args.config}; run without --check", file=sys.stderr)
        return 1
    target = Path(args.out or plugin)
    if result != source or target != plugin:
        target.write_text(result)
    print(f"{'wrote' if result != source else 'unchanged'} {target}: {len(config['Layout']['Entries'])} entries, "
          f"{len(config['Layout']['Connections'])} connections")
    if args.no_verify:
        return 0
    if compare(config, serialize(result), "new Configuration() serialized", args.config):
        print("the settings above are not written by this tool; edit their defaults by hand", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
