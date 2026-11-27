import argparse
import json
from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parent
MOD_ID = "iMoonDay.GMCMConfigTextLocalizer"
DEFAULT_MODS = Path("E:/SteamLibrary/steamapps/common/Stardew Valley/Mods")
PACK_PATH = re.compile(r"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*/[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*\.json")
JSON_STRING = r'"(?:[^"\\]|\\.)*"'
JSON_COMMENTS = re.compile(JSON_STRING + r"|//[^\r\n]*|/\*[\s\S]*?\*/")
TRAILING_COMMAS = re.compile(JSON_STRING + r"|,(?=\s*[}\]])")


def git(*args, capture=False):
    return subprocess.run(
        ["git", *args], cwd=ROOT, check=True, stdout=subprocess.PIPE if capture else None
    ).stdout


def read_object(text, label):
    def unique_keys(pairs):
        values = {}
        for key, value in pairs:
            if not key.strip() or key in values:
                raise ValueError(f"Invalid or duplicate key {key!r}")
            values[key] = value
        return values

    # Match complete strings first so comment and comma handling preserves source text.
    def preserve_string(token):
        return token[0] if token[0].startswith('"') else " "

    without_comments = JSON_COMMENTS.sub(preserve_string, text)
    clean = TRAILING_COMMAS.sub(preserve_string, without_comments)
    try:
        values = json.loads(clean, object_pairs_hook=unique_keys)
        if not isinstance(values, dict):
            raise ValueError("Expected a JSON object.")
        return values
    except ValueError as error:
        raise ValueError(f"{label}: {error}") from error


def read_local(path):
    return read_object(path.read_text(encoding="utf-8-sig"), path)


def find_overrides():
    matches = []
    for manifest in DEFAULT_MODS.glob("*/manifest.json"):
        try:
            mod_id = read_local(manifest).get("UniqueID")
        except (OSError, ValueError):
            continue
        if isinstance(mod_id, str) and mod_id.casefold() == MOD_ID.casefold():
            matches.append(manifest.parent / "overrides")
    if len(matches) != 1:
        raise ValueError("Could not find a unique installation. Pass the overrides folder as an argument.")
    return matches[0]


def collect_updates(overrides):
    if not overrides.is_dir():
        raise ValueError(f"Overrides folder does not exist: {overrides}")

    tracked = git("ls-tree", "-r", "--name-only", "-z", "HEAD", "--", "overrides", capture=True)
    tracked_paths = {path.casefold(): path for path in tracked.decode("utf-8").split("\0") if path}
    updates = {}
    for source in sorted(overrides.glob("*/*.json")):
        if source.parent.name.startswith("."):
            continue
        relative = source.relative_to(overrides).as_posix()
        if PACK_PATH.fullmatch(relative) is None:
            raise ValueError(f"Invalid override path: {relative}")
        path = tracked_paths.get(f"overrides/{relative}".casefold(), f"overrides/{relative}")
        target = ROOT / path
        original = {}
        if path.casefold() in tracked_paths:
            original = read_object(git("show", f"HEAD:{path}", capture=True).decode("utf-8"), path)
        marker_locale = "zh" if source.parent.name.casefold() == "zh-cn" else source.parent.name
        marker_file = overrides / ".ignored" / f"{marker_locale}.json"
        markers = read_local(marker_file) if marker_file.exists() else {}
        ignored = next((values for key, values in markers.items() if key.casefold() == source.stem.casefold()), [])
        if not isinstance(ignored, list) or any(not isinstance(key, str) for key in ignored):
            raise ValueError(f"{marker_file}: expected a list of marked source texts.")
        ignored = set(ignored)
        local = read_local(source)
        if any(value is not None and not isinstance(value, str) for value in local.values()):
            raise ValueError(f"{source}: expected string or null translations.")
        changed = {
            key: value for key, value in local.items()
            if value and value.strip() and value != original.get(key)
            and key not in ignored
        }
        if not changed:
            continue
        merged = original | changed
        if target.exists() and read_local(target) not in (original, merged):
            raise ValueError(f"Uncommitted translations in {path}. Review them before publishing.")
        if path in updates:
            raise ValueError(f"Multiple override files map to {path}.")
        updates[path] = merged
        added = len(changed.keys() - original.keys())
        print(f"{path}: {added} new, {len(changed) - added} revised")
    return updates


def main():
    parser = argparse.ArgumentParser(description="Publish local GMCM translations through GitHub Actions.")
    parser.add_argument("overrides", type=Path, nargs="?", help="Path to the installed mod's overrides folder")
    parser.add_argument("--dry-run", action="store_true", help="List translation changes without writing or pushing")
    args = parser.parse_args()
    overrides = args.overrides if args.overrides is not None else find_overrides()
    if args.dry_run:
        if not collect_updates(overrides):
            print("No translation changes.")
        return

    if git("branch", "--show-current", capture=True).strip() != b"main":
        raise ValueError("Run this command from the main branch to publish translations.")
    git("fetch", "origin", "main")
    git("merge", "--ff-only", "FETCH_HEAD")
    ahead = git("log", "--format=", "--name-only", "-z", "FETCH_HEAD..HEAD", capture=True)
    if any(not path.strip().startswith(b"overrides/") for path in ahead.split(b"\0") if path.strip()):
        raise ValueError("Local commits include changes outside overrides/. Publish those separately first.")

    updates = collect_updates(overrides)
    staged = git("diff", "--cached", "--name-only", "-z", capture=True).decode("utf-8").split("\0")
    if updates.keys() & set(staged):
        raise ValueError("Some translations to publish are already staged. Unstage them or commit them first.")
    for path, values in updates.items():
        target = ROOT / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(values, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    if updates:
        subprocess.run([sys.executable, ".github/validate-packs.py"], cwd=ROOT, check=True)
        git("add", "--intent-to-add", "--", *updates)
        git("commit", "--only", "-m", "Update community translations", "--", *updates)
    if updates or ahead.strip():
        git("push", "origin", "HEAD:main")
    else:
        print("No translation changes.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from error
