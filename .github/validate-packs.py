import json
from pathlib import Path
import re


def read_translations(pairs):
    translations = {}
    for source, translation in pairs:
        if not source.strip() or source in translations or not isinstance(translation, str):
            raise ValueError(f"Invalid or duplicate translation for {source!r}")
        translations[source] = translation
    return translations


paths = set()
expanded_size = 0
for path in sorted(Path("overrides").rglob("*")):
    if not path.is_file():
        continue
    relative = path.relative_to("overrides").as_posix()
    if not re.fullmatch(r"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*/[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*\.json", relative):
        raise SystemExit(f"Invalid pack path: {path}")
    if relative.lower() in paths:
        raise SystemExit(f"Duplicate pack path: {path}")
    paths.add(relative.lower())
    size = path.stat().st_size
    expanded_size += size
    if size > 1024 * 1024 or expanded_size > 32 * 1024 * 1024:
        raise SystemExit(f"Translation pack size limit exceeded: {path}")
    try:
        translations = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=read_translations)
    except ValueError as error:
        raise SystemExit(f"{path}: {error}") from error
    if not isinstance(translations, dict):
        raise SystemExit(f"Expected a JSON object: {path}")

if not paths:
    raise SystemExit("No translation packs found.")
print(f"Validated {len(paths)} translation packs.")
