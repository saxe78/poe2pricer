#!/usr/bin/env python3
"""Die Runen der Expedition (data/runes) aus den Spieldaten bauen: Namen, Wirkungstexte, Bilder.

Quellen, alle Grinding Gear Games, gelesen aus der Steam-Installation:

  - Tabellen Expedition2Runes, ArchnemesisMods und Mods (Export mit poe-dat aus der
    Werkzeugkiste, `poe-dat.mjs export <ordner> --sprache English --sprache German --sprache
    Russian Expedition2Runes ArchnemesisMods Mods:Id,Name`): je Rune ihre Id und ihr
    Archnemesis-Modifikator, dessen Mod-Name den Anzeigetext trägt — Name der Rune,
    „Monsters gain:" und die Wirkungszeilen — in jeder Sprache des Clients.
  - Die Bilder der Runen, wie das Spiel sie auf dem Balken über einem Relikt (RemnantArt, 60 px)
    und in der Relikt-Liste (TomeArt, 50 px) zeichnet: aus den Bundles mit der Werkzeugkiste,
    `poe-dat.mjs dateien <ordner> <bildname>…` (Bildnamen = die Spalten TomeArt und RemnantArt
    der Tabelle; das Werkzeug schneidet nach dem Sprite-Index zu und legt <ordner>/<bildname>.png ab).

  tools/rune_data.py --dat <ordner> --icons <ordner> --out data/runes

--dat: der Export-Ordner mit English/, German/, Russian/ (je Expedition2Runes.json,
ArchnemesisMods.json, Mods.json). --icons: der Zielordner von `poe-dat.mjs dateien` mit den
PNG-Dateien unter ihrem Bildpfad. Geschrieben werden runes.json, tome/<Id>.png, remnant/<Id>.png. Die Klasse je Rune
(loot, weaker, danger, neutral) ist die Vorgabe des Programms; der Spieler ändert sie im
Programm, nicht hier.
"""
import argparse
import json
import os
import re
import sys

# Vorgabe-Klassen (01.11.2026, aus den Wirkungen): Beute, wenn die Rune Seltenheit oder die
# Zahl der Rares hebt; schwächer, wenn sie nur mehr oder stärkere Monster bringt; neutral, wenn
# sie nur Erfahrung gibt; sonst Gefahr.
CLASSES = {
    "Power": "loot", "Bond": "loot", "Time": "loot", "Opulent": "loot",
    "Death": "weaker", "Oath": "weaker",
    "Wisdom": "neutral",
}
LANGS = {"English": "en", "German": "de", "Russian": "ru"}
# Zielordner je Bildsatz und die Spalte der Tabelle, die den Bildpfad nennt.
ART = {"tome": "TomeArt", "remnant": "RemnantArt"}


def plain(markup):
    """Der Anzeigetext ohne Auszeichnung: <<Bild>> weg, <rgb(…)>{x} und <font…>{<italic>{x}} zu x,
    [Begriff|Text] zu Text, [Begriff] zu Begriff. Zeilen getrimmt, leere weg."""
    s = re.sub(r"<<[^>]*>>", "", markup)
    for _ in range(6):
        s = re.sub(r"<[^<>{}]*>\{([^{}]*)\}", r"\1", s)
    s = re.sub(r"\[([^\[\]|]*)\|([^\[\]]*)\]", r"\2", s)
    s = re.sub(r"\[([^\[\]]*)\]", r"\1", s)
    return [line.strip() for line in s.splitlines() if line.strip()]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dat", required=True)
    ap.add_argument("--icons", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    import shutil

    runes = json.load(open(os.path.join(args.dat, "English", "Expedition2Runes.json"), encoding="utf-8"))
    arch = json.load(open(os.path.join(args.dat, "English", "ArchnemesisMods.json"), encoding="utf-8"))
    # Der Anzeigetext steht am Mod des Archnemesis-Eintrags (ArchnemesisMods.Name ist leer).
    names = {}
    for lang in LANGS:
        rows = json.load(open(os.path.join(args.dat, lang, "Mods.json"), encoding="utf-8"))
        names[lang] = {m["_index"]: m["Name"] for m in rows}
    out = []
    for r in runes:
        if r["Mod"] is None:  # „Bait“: kein Modifikator, nur für Krillsons Schlüssel
            continue
        entry = {"id": r["Id"], "rare": "RareRune" in r["RemnantArt"], "class": CLASSES.get(r["Id"], "danger"), "name": {}, "effect": {}}
        for lang, code in LANGS.items():
            lines = plain(names[lang][arch[r["ArchnemesisMod"]]["Mod"]])
            entry["name"][code] = lines[0]
            entry["effect"][code] = lines[1:]
        for kind, column in ART.items():
            src = os.path.join(args.icons, r[column] + ".png")
            if not os.path.exists(src):
                sys.exit(f"fehlt: {src} — mit `poe-dat.mjs dateien {args.icons} {r[column]}` holen")
            os.makedirs(os.path.join(args.out, kind), exist_ok=True)
            shutil.copy2(src, os.path.join(args.out, kind, f"{r['Id']}.png"))
        out.append(entry)
    doc = {
        "source": "Grinding Gear Games, Path of Exile 2: tables Expedition2Runes and ArchnemesisMods, pictures Expedition/Tome and Expedition/Remnant",
        "builtWith": "tools/rune_data.py",
        "runes": out,
    }
    with open(os.path.join(args.out, "runes.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    print(f"{len(out)} Runen nach {args.out}")
    for e in out:
        print(f"  {e['id']:14s} {e['class']:8s} {e['name']['en']:20s} | {e['name']['de']:22s} | " + " / ".join(e["effect"]["en"]))


if __name__ == "__main__":
    main()
