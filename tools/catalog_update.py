#!/usr/bin/env python3
"""Den Katalog der Erkennung (data/catalog) aus den Quellen von Grinding Gear Games neu bauen.

Der Katalog sagt der Erkennung, welche Gegenstände es gibt, wie sie heißen, wie viele Felder sie
im Ritual-Fenster belegen und wie sie aussehen. Drei Quellen, alle GGG:

  - poe.ninja, die Kategorien aus NinjaClient.cs: jeder Gegenstand, den poe.ninja kennt, mit
    Name und Bild-URL; bei Uniques stecken Breite und Höhe in Feldern in der Bild-URL.
    Gefragt werden die laufende Softcore-Liga und Standard (Uniques ohne Preis in der einen
    Liga haben ihn oft in der anderen).
  - https://www.pathofexile.com/api/trade2/data/static: alle Währungs-Gegenstände der
    Trade-Seite, auch die, für die poe.ninja nichts listet (Relikte, kleine Essenzen, Runen).
  - https://web.poecdn.com: die Bilder, wie das Spiel sie zeichnet (PNG mit Alphakanal).

Die Ids folgen der Regel von Dev-Jahn/poe2-ritual-checker (Name ohne Apostroph, Leerzeichen
als Unterstrich), damit Sollwertdateien, Tests und recognition-model.json weiter passen; das
Skript prüft das gegen den bisherigen Katalog. recognition-model.json wird mit der neuen
Katalogversion gestempelt und mitkopiert.

  tools/catalog_update.py --out <ordner> [--existing data/catalog] [--cache <ordner>]
                          [--league <name>] [--fresh] [--report-only]
                          [--unique-pictures poecdn|poe2db]

--out: Zielordner (catalog.json, images/, recognition-model.json, report.txt); erst in einen
  Probeordner, nach der Messung (Poe2Pricer.Cli analyze über testdata/favours, tools/evaluate.py)
  nach data/catalog. --existing: der bisherige Katalog, für die Id-Prüfung, das Modell und den
  Bericht (neu, weggefallen, andere Maße).
--cache: heruntergeladene Antworten und Bilder; --fresh verwirft die Antworten, nie die Bilder.
--unique-pictures: woher die Bilder der Uniques kommen. poecdn (Vorgabe): GGGs Bilddienst,
  47 px je Feld. poe2db: https://cdn.poe2db.tw/image/Art/<Kunstpfad>.webp, 108 px je Feld, nach
  PNG gewandelt (braucht Pillow); der Kunstpfad stammt weiter aus der GGG-URL. Währung kommt
  immer von poecdn (64 px, darauf sind die Varianten in Tuning.cs gemessen).
--report-only: nur der Bericht (was fehlt dem Katalog, was ist weggefallen), keine Bilder, kein
  Katalog — der schnelle Abgleich nach einem Spielpatch oder einer neuen Liga.
"""
import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

NINJA = "https://poe.ninja"
STATIC = "https://www.pathofexile.com/api/trade2/data/static"
POECDN = "https://web.poecdn.com"
AGENT = "poe2pricer catalog_update (+https://github.com/saxe78/poe2pricer)"
# Wie NinjaClient.ExchangeTypes und UniqueTypes.
EXCHANGE = ["Ritual", "Currency", "Fragments", "Essences", "SoulCores", "Idols", "Runes", "Delirium",
            "Verisium", "Expedition", "Abyss", "Breach", "UncutGems", "LineageSupportGems"]
UNIQUES = ["UniqueArmours", "UniqueWeapons", "UniqueAccessories", "UniqueJewels", "UniqueFlasks",
           "UniqueCharms", "UniqueSanctumRelics", "UniqueTablets", "PrecursorTablets"]
PAUSE = 1.1   # Sekunden zwischen zwei Anfragen an poe.ninja (NinjaClient: eine je Sekunde)
IMAGE_PAUSE = 0.15


def id_of(name):
    """Dev-Jahns Id-Regel (poe2db-Slug): Apostroph weg, Leerzeichen als Unterstrich, Satzzeichen
    weg, Nicht-ASCII prozentkodiert (Mjölner -> Mj%C3%B6lner)."""
    s = name.replace("'", "").replace(" ", "_")
    s = re.sub(r"[:,()\"]", "", s)
    return urllib.parse.quote(s, safe="_-")


def payload(url):
    """Der Bildpfad in einer poecdn-URL: /gen/image/<base64>/<sig>/<Name>.png, base64 ist
    [25,14,{"f":"2DItems/...","w":2,"h":3,...}]."""
    try:
        b = url.split("/gen/image/")[1].split("/")[0]
        return json.loads(base64.b64decode(b + "=" * (-len(b) % 4)))[2]
    except (IndexError, ValueError, TypeError):
        return {}


class Fetcher:
    def __init__(self, cache, fresh):
        self.cache = cache
        os.makedirs(os.path.join(cache, "json"), exist_ok=True)
        os.makedirs(os.path.join(cache, "images"), exist_ok=True)
        if fresh:
            for f in os.listdir(os.path.join(cache, "json")):
                os.remove(os.path.join(cache, "json", f))
        self.last = 0.0

    def _get(self, url, pause):
        wait = self.last + pause - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        req = urllib.request.Request(url, headers={"User-Agent": AGENT})
        with urllib.request.urlopen(req, timeout=60) as r:
            data = r.read()
        self.last = time.monotonic()
        return data

    def json(self, url, key):
        path = os.path.join(self.cache, "json", key + ".json")
        if not os.path.exists(path):
            data = self._get(url, PAUSE)
            with open(path, "wb") as f:
                f.write(data)
        with open(path, "rb") as f:
            return json.load(f)

    def image(self, url):
        """Das Bild unter cache/images/<sha256(url)[:24]>.png; Rückgabe: Pfad. Ein WebP (poe2db)
        wird nach PNG gewandelt. Antwort 403 oder 429 (Drossel): nach einer Pause noch zweimal."""
        name = hashlib.sha256(url.encode()).hexdigest()[:24] + ".png"
        path = os.path.join(self.cache, "images", name)
        if not os.path.exists(path):
            for attempt in range(3):
                try:
                    data = self._get(url, IMAGE_PAUSE)
                    break
                except urllib.error.HTTPError as e:
                    if e.code not in (403, 429) or attempt == 2:
                        raise
                    time.sleep(10 * (attempt + 1))
            if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
                import io
                from PIL import Image
                buf = io.BytesIO()
                Image.open(io.BytesIO(data)).convert("RGBA").save(buf, "PNG")
                data = buf.getvalue()
            if data[:8] != b"\x89PNG\r\n\x1a\n":
                raise ValueError(f"kein PNG: {url}")
            with open(path, "wb") as f:
                f.write(data)
        return path


def leagues_of(fetcher, wanted):
    names = [l["id"] for l in fetcher.json(f"{NINJA}/poe2/api/economy/leagues", "leagues")]
    current = wanted or next((l for l in names if not l.startswith("HC ") and l not in ("Standard", "Hardcore")), "Standard")
    return [l for l in dict.fromkeys([current, "Standard"]) if l in names]


def collect(fetcher, leagues, log):
    """name -> {kind, url, art, w, h, source}; die erste Quelle je Name gewinnt, bei Uniques
    die Zeile mit den meisten Angeboten (ohne corrupted, Runeforged, Runemastered — wie
    PriceBook.AddUniques)."""
    found = {}

    def add(name, kind, url, source, listings=0):
        if not url:
            return
        if url.startswith("/"):
            url = POECDN + url
        p = payload(url)
        entry = {"kind": kind, "url": url, "art": p.get("f", ""), "w": p.get("w"), "h": p.get("h"),
                 "source": source, "listings": listings}
        old = found.get(name)
        if old is None or (kind == "unique" and listings > old["listings"]):
            found[name] = entry

    static = fetcher.json(STATIC, "trade2-static")["result"]
    for group in static:
        for e in group["entries"]:
            add(e["text"], "currency", e.get("image"), f"{STATIC} ({group['id']})")
    log(f"trade2/data/static: {sum(len(g['entries']) for g in static)} Einträge, {len(found)} mit Bild")
    for league in leagues:
        q = urllib.parse.quote(league)
        for t in EXCHANGE:
            j = fetcher.json(f"{NINJA}/poe2/api/economy/exchange/current/overview?league={q}&type={t}", f"exchange-{t}-{league}")
            for it in j.get("items", []):
                add(it["name"], "currency", it.get("image"), f"poe.ninja exchange {t} ({league})")
        for t in UNIQUES:
            j = fetcher.json(f"{NINJA}/poe2/api/economy/stash/current/item/overview?league={q}&type={t}", f"unique-{t}-{league}")
            for l in j.get("lines", []):
                b = l.get("baseType") or ""
                if l.get("corrupted") or b.startswith("Runeforged ") or b.startswith("Runemastered "):
                    continue
                add(l["name"], "unique", l.get("icon"), f"poe.ninja {t} ({league})", l.get("listingCount") or 0)
        log(f"poe.ninja {league}: zusammen {len(found)} Namen")
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--existing", default="data/catalog")
    ap.add_argument("--cache", default=os.path.join(tempfile.gettempdir(), "poe2pricer-catalog-cache"))
    ap.add_argument("--league")
    ap.add_argument("--fresh", action="store_true")
    ap.add_argument("--report-only", action="store_true")
    ap.add_argument("--unique-pictures", choices=["poecdn", "poe2db"], default="poecdn")
    a = ap.parse_args()
    report = []

    def log(line):
        print(line)
        report.append(line)

    existing_path = os.path.join(a.existing, "catalog.json")
    existing = {}
    if os.path.exists(existing_path):
        with open(existing_path, encoding="utf-8") as f:
            existing = {i["id"]: i for i in json.load(f)["items"]}
        wrong = [(i["nameEn"], i["id"]) for i in existing.values() if id_of(i["nameEn"]) != i["id"]]
        if wrong:
            sys.exit(f"Id-Regel passt nicht zum bisherigen Katalog: {wrong[:5]}")
        log(f"bisheriger Katalog: {len(existing)} Einträge, Id-Regel bestätigt")

    fetcher = Fetcher(a.cache, a.fresh)
    leagues = leagues_of(fetcher, a.league)
    log(f"Ligen: {', '.join(leagues)}")
    found = collect(fetcher, leagues, log)

    os.makedirs(a.out, exist_ok=True)
    if a.report_only:
        new = sorted(f"{e['kind']:8} {n}  ({e['source']})" for n, e in found.items() if id_of(n) not in existing)
        gone = sorted(i["nameEn"] for i in existing.values() if i["nameEn"] not in found)
        log(f"\nNICHT IM KATALOG: {len(new)}")
        report.extend("  " + x for x in new)
        log(f"WEGGEFALLEN (im Katalog, in keiner Quelle): {len(gone)}")
        report.extend("  " + x for x in gone)
        with open(os.path.join(a.out, "report.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(report) + "\n")
        return
    os.makedirs(os.path.join(a.out, "images"), exist_ok=True)
    items = []

    new, resized, rekinded, failed, unsized, fallback = [], [], [], [], [], []
    for n, (name, e) in enumerate(sorted(found.items()), 1):
        cid = id_of(name)
        old = existing.get(cid)
        picture = e["url"]
        if e["kind"] == "unique" and a.unique_pictures == "poe2db" and e["art"]:
            # poe2db legt die Kunst nicht immer unter GGGs Pfad ab (Skysliver: ohne "Uniques/");
            # dann bleibt es beim Bild von poecdn, und der Bericht nennt es.
            picture = f"https://cdn.poe2db.tw/image/Art/{urllib.parse.quote(e['art'])}.webp"
            try:
                fetcher.image(picture)
            except Exception as ex:
                fallback.append(f"{name}: {ex} — Bild von poecdn")
                picture = e["url"]
        try:
            src = fetcher.image(picture)
        except Exception as ex:  # ein fehlendes Bild soll den Lauf nicht beenden
            failed.append(f"{name}: {ex}")
            continue
        image = "images/" + os.path.basename(src)
        shutil.copy(src, os.path.join(a.out, image))
        # Breite und Höhe in Feldern stehen bei Uniques in der Bild-URL; sonst (Währung, ein paar
        # Questgegenstände) bleibt es beim bisherigen Maß oder bei 1x1.
        if e["w"] is None or e["h"] is None:
            # Manche Pfade nennen das Maß selbst (VerisiumOre_Starlit_2x4_Armour).
            named = re.search(r"_(\d)x(\d)(?:_|$)", e["art"])
            e["w"], e["h"] = (int(named[1]), int(named[2])) if named else (old["width"], old["height"]) if old else (1, 1)
            if e["kind"] == "unique":
                unsized.append(f"{name}: {e['w']}x{e['h']}" + (" (bisheriges Maß)" if old else " (angenommen)"))
        item = {"id": cid, "nameEn": name, "nameKo": old.get("nameKo", "") if old else "", "kind": e["kind"],
                "image": image, "width": e["w"], "height": e["h"], "source": e["source"],
                "imageSource": picture, "art": e["art"], "maxStackSize": old.get("maxStackSize") if old else None}
        items.append(item)
        if old is None:
            new.append(f"{e['kind']:8} {name}  ({e['source']})")
        else:
            if (old["width"], old["height"]) != (e["w"], e["h"]):
                resized.append(f"{name}: {old['width']}x{old['height']} -> {e['w']}x{e['h']}")
            if old["kind"] != e["kind"]:
                rekinded.append(f"{name}: {old['kind']} -> {e['kind']}")
        if n % 100 == 0:
            print(f"  {n}/{len(found)} Bilder")
    gone = sorted(i["nameEn"] for i in existing.values() if i["nameEn"] not in found)

    items.sort(key=lambda i: (i["kind"], i["nameEn"]))
    version = hashlib.sha256(json.dumps(items, sort_keys=True).encode()).hexdigest()[:16]
    catalog = {"version": version, "retrievedAt": datetime.now(timezone.utc).isoformat(), "items": items}
    with open(os.path.join(a.out, "catalog.json"), "w", encoding="utf-8") as f:
        json.dump(catalog, f, ensure_ascii=False, indent=2)
    model_path = os.path.join(a.existing, "recognition-model.json")
    if os.path.exists(model_path):
        with open(model_path, encoding="utf-8") as f:
            model = json.load(f)
        model["catalogVersion"] = version
        with open(os.path.join(a.out, "recognition-model.json"), "w", encoding="utf-8") as f:
            json.dump(model, f, ensure_ascii=False, separators=(",", ":"))

    log(f"\nKatalog {version}: {len(items)} Einträge "
        f"({sum(1 for i in items if i['kind'] == 'currency')} currency, {sum(1 for i in items if i['kind'] == 'unique')} unique)")
    log(f"NEU: {len(new)}")
    report.extend("  " + x for x in new)
    log(f"WEGGEFALLEN (bisher im Katalog, in keiner Quelle): {len(gone)}")
    report.extend("  " + x for x in gone)
    log(f"ANDERE MASSE: {len(resized)}")
    report.extend("  " + x for x in resized)
    log(f"ANDERE ART: {len(rekinded)}")
    report.extend("  " + x for x in rekinded)
    log(f"UNIQUE OHNE MASS IN DER URL: {len(unsized)}")
    report.extend("  " + x for x in unsized)
    log(f"UNIQUE OHNE BILD BEI POE2DB (Bild von poecdn): {len(fallback)}")
    report.extend("  " + x for x in fallback)
    log(f"BILD FEHLGESCHLAGEN: {len(failed)}")
    report.extend("  " + x for x in failed)
    with open(os.path.join(a.out, "report.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(report) + "\n")


if __name__ == "__main__":
    main()
