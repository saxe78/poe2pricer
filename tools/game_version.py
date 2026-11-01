#!/usr/bin/env python3
"""Die Version des installierten Spiels lesen, für den Vermerk „gebaut für …“ in CHANGELOG und
README (OFFEN P5). Quelle: das Spielprotokoll. Bei jedem Start schreibt der Client in
logs/LatestClient.txt (und fortlaufend in logs/Client.txt) seinen User-Agent
„PoE poe2_production/tags/0.5.5e Windows x64“; die Marke dahinter ist die Patch-Version, die
der Anmeldebildschirm zeigt. Die Exe trägt keine Versionsressource, nur denselben String.

  tools/game_version.py [--spiel <ordner>]

Ohne --spiel: POE2_DIR, sonst die Steam-Bibliotheken unter /mnt/<laufwerk>/{Steam,SteamLibrary,
Program Files (x86)/Steam} (wie poe-dat in der Werkzeugkiste). Ausgabe: Version, Datum des
letzten Starts, Spielordner. Gelesen wird nur; am Spiel ändert sich nichts.
"""
import argparse
import os
import re
import sys

GAME = "Path of Exile 2"
TAG = re.compile(r"^(\S+ \S+) .*poe2_production/tags/(\S+)", re.M)


def find_game(given):
    if given:
        return given
    if os.environ.get("POE2_DIR"):
        return os.environ["POE2_DIR"]
    roots = []
    if os.path.isdir("/mnt"):
        for drive in sorted(os.listdir("/mnt")):
            for steam in ("Steam", "SteamLibrary", "Program Files (x86)/Steam"):
                roots.append(os.path.join("/mnt", drive, steam))
    found = [os.path.join(r, "steamapps", "common", GAME) for r in roots]
    found = [f for f in found if os.path.isfile(os.path.join(f, "logs", "LatestClient.txt"))]
    if not found:
        sys.exit("Kein Spielordner mit logs/LatestClient.txt gefunden — mit --spiel <ordner> oder POE2_DIR angeben.")
    return max(found, key=lambda f: os.path.getmtime(os.path.join(f, "logs", "LatestClient.txt")))


def version_of(folder):
    """(Version, Zeitstempel der Zeile) aus LatestClient.txt, sonst aus dem Ende von Client.txt."""
    for name, tail in (("LatestClient.txt", None), ("Client.txt", 4_000_000)):
        path = os.path.join(folder, "logs", name)
        if not os.path.isfile(path):
            continue
        with open(path, "rb") as f:
            if tail:
                f.seek(max(0, os.path.getsize(path) - tail))
            text = f.read().decode("utf-8", errors="replace")
        hits = TAG.findall(text)
        if hits:
            when, version = hits[-1]
            return version, when
    sys.exit(f"Keine Versionszeile (poe2_production/tags/…) in {folder}/logs.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--spiel")
    a = ap.parse_args()
    folder = find_game(a.spiel)
    version, when = version_of(folder)
    print(f"{version}  (Start {when}, {folder})")


if __name__ == "__main__":
    main()
