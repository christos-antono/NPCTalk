"""Before you publish: block what must never ship with a talking-NPC project.

  npctalk check .               exit 1 and list every problem
  npctalk check examples/my-mod

It flags:
  - audio: voice clips, game audio, renders (.wav .mp3 .ogg .flac .m4a .opus .aac .webm .aiff .wma). Cloned
    voices of real people and audio cut from a game are not yours to publish.
  - game audio banks as extracted: RAGE .awc (GTA V, RDR2), Wwise .wem .bnk, FMOD .fsb .bank, Bethesda .xwm
  - transcripts: .lab files (they sit next to voice clips and are often a game's dialogue)
  - video outside docs/media/ (demo clips carry cloned voices too; keep a deliberate, approved one in
    docs/media/)
  - keys and tokens: fal, Cerebras, OpenAI-style, Hugging Face, GitHub, Modal, ElevenLabs, bearer tokens
    written out, .env files
  - real Modal URLs (https://<workspace>--npcvoice-drama.modal.run): /health needs no token, so anyone with
    the URL can wake your GPU. Use your-workspace in examples.

In a git repo only the files git would commit are checked (tracked + untracked, not ignored).
"""
import re
import subprocess
import sys
from pathlib import Path

AUDIO = {".wav", ".mp3", ".ogg", ".flac", ".m4a", ".opus", ".aac", ".webm", ".aiff", ".aif", ".wma"}
GAME_AUDIO = {".awc", ".wem", ".bnk", ".fsb", ".bank", ".xwm"}
VIDEO = {".mp4", ".mkv", ".mov", ".avi"}
TEXT = {".py", ".cs", ".ts", ".js", ".md", ".txt", ".ini", ".json", ".toml", ".yml", ".yaml", ".ps1", ".sh",
        ".cmd", ".bat", ".cfg", ".csproj", ".xml", ".env", ""}
SECRETS = [
    ("fal key", re.compile(r"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[0-9a-f]{32}\b")),
    ("Cerebras key", re.compile(r"\bcsk-[a-z0-9]{24,}\b")),
    ("OpenAI-style key", re.compile(r"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{24,}")),
    ("ElevenLabs key", re.compile(r"\bsk_[a-f0-9]{40,}\b")),
    ("Hugging Face token", re.compile(r"\bhf_[A-Za-z0-9]{30,}\b")),
    ("GitHub token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{30,}\b")),
    ("Modal token", re.compile(r"\ba[ks]-[A-Za-z0-9]{20,}\b")),
    ("bearer token", re.compile(r"Bearer\s+(?![<$\{])[A-Za-z0-9._~+/-]{24,}")),
]
MODAL_URL = re.compile(r"\b([a-z0-9][a-z0-9-]*)--[a-z0-9-]+(?:\.[a-z0-9-]+)?\.modal\.run")
PLACEHOLDER_WORKSPACES = {"your-workspace", "workspace", "your-org", "example"}


def register(sub):
    import argparse

    p = sub.add_parser("check", help="nothing that mustn't ship: audio, transcripts, keys, real server URLs",
                       description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("path", nargs="?", default=".", help="folder to check (default: .)")
    p.set_defaults(run=run)


def files(root):
    root = Path(root)
    try:
        out = subprocess.run(["git", "-C", str(root), "ls-files", "-co", "--exclude-standard", "-z"],
                             capture_output=True, check=True).stdout
        return [root / f for f in out.decode("utf-8", "replace").split("\0") if f]
    except (subprocess.CalledProcessError, FileNotFoundError):
        return [p for p in root.rglob("*") if p.is_file() and ".git" not in p.parts]


def problems(root):
    root = Path(root)
    found = []
    for path in files(root):
        rel = path.relative_to(root).as_posix()
        suffix = path.suffix.lower()
        if suffix in AUDIO:
            found.append((rel, "audio file (voice clips and game audio don't ship)"))
            continue
        if suffix in GAME_AUDIO:
            found.append((rel, "game audio bank (extracted game audio doesn't ship)"))
            continue
        if suffix == ".lab":
            found.append((rel, "transcript of a voice clip (.lab)"))
            continue
        if suffix in VIDEO and not rel.startswith("docs/media/"):
            found.append((rel, "video outside docs/media/"))
            continue
        if path.name == ".env" or path.name.startswith(".env."):
            found.append((rel, ".env file"))
            continue
        if suffix not in TEXT or not path.exists() or path.stat().st_size > 2_000_000:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for n, line in enumerate(text.splitlines(), 1):
            for label, pattern in SECRETS:
                if pattern.search(line):
                    found.append((f"{rel}:{n}", label))
            for m in MODAL_URL.finditer(line):
                if m.group(1) not in PLACEHOLDER_WORKSPACES:
                    found.append((f"{rel}:{n}", f"real Modal URL ({m.group(1)}--...): use your-workspace"))
    return found


def run(args):
    found = problems(args.path)
    for where, what in found:
        print(f"{where}: {what}")
    if found:
        print(f"\n{len(found)} problem(s): fix them before you publish")
        return 1
    print("nothing that shouldn't ship")
    return 0


if __name__ == "__main__":
    sys.exit(run(type("A", (), {"path": sys.argv[1] if len(sys.argv) > 1 else "."})))
