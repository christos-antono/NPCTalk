"""Reference voices for a Fish Audio server: from a recording you have the rights to, to the clips it clones.

  npctalk voice make --name Narrator --audio narrator.wav                  # -> voices/Narrator/
  npctalk voice make --name Narrator --audio narrator_angry.wav --mood angry  # -> voices/Narrator/angry/
  npctalk voice upload voices/Narrator                                     # -> the server's /voices/Narrator

  # transcribe on open weights on your own GPU (server/asr.py) or PC instead of fal:
  npctalk voice make --name Narrator --audio narrator.wav --stt-url https://your-workspace--npcvoice-asr.modal.run/v1
  npctalk voice make --name Narrator --audio narrator.wav --stt-url http://127.0.0.1:8000/v1 --stt-model Systran/faster-whisper-large-v3

make splits the recording into lines at pauses, joins runs of neighbouring lines into 6-11 s candidates,
transcribes them (fal's NVIDIA Nemotron ASR endpoint with FAL_KEY, or with --stt-url any OpenAI-compatible
transcription server: Nemotron's open weights via server/asr.py, Whisper or Parakeet locally), drops the ones
that are mostly screams, grunts, music or another language, and writes:

  voices/<Name>/clone.wav + .lab     ~25 s of the cleanest candidates joined: what the server clones from by
                                     default (on Fish Audio Drama 3 it beats any single clip)
  voices/<Name>/ref_0..2.wav + .lab  the three cleanest single candidates (engines that want a short clip)
  voices/<Name>/<mood>/ref_0..1      with --mood: clips the server uses for lines tagged [<mood>]

Candidates and their transcripts stay in voices_work/<Name>/ to listen to. A good recording is one
speaker, no music, normal speaking level, a minute or more of lines. Needs ffmpeg on PATH.

upload copies a voice folder into the server's Modal volume (npcvoice-models, /voices/<Name>) with the
modal CLI. The server encodes every voice in /voices when it starts, so restart it afterwards
(`modal app stop npcvoice-drama`; the next request starts a fresh one). Until then, games can send the
clip by URL (`reference_audio_url`).

Only clone voices you have the rights to: your own, an actor who agreed, or a licensed voice. Keep game
audio and clones of real people's voices out of any public repo (`npctalk check` blocks audio files).
"""
import base64
import json
import os
import shutil
import subprocess
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path

SR = 16000              # analysis rate
FRAME = SR // 50        # 20 ms frames
SPEECH_DB = -42.0       # a frame louder than this is speech
SPLIT_SILENCE = 0.3     # a pause this long ends a line
PAD = 0.08              # kept around each line
GAP = 0.25              # pause between joined lines
MIN_REF, MAX_REF, CLONE_SECONDS = 6.0, 11.0, 24.0
STT = "nvidia/nemotron-asr-multilingual/asr"            # on fal
STT_OPEN = "nvidia/nemotron-3.5-asr-streaming-0.6b"     # the open weights, for --stt-url servers


@dataclass
class Line:
    start: float
    end: float
    rms: float   # dBFS
    peak: float  # 0..1


@dataclass
class Candidate:
    lines: list
    dur: float
    score: float
    wav: str = ""
    text: str = ""
    words: int = field(default=0)


def register(sub):
    import argparse

    p = sub.add_parser("voice", help="make reference voices and upload them to the server", description=__doc__,
                       formatter_class=argparse.RawDescriptionHelpFormatter)
    vsub = p.add_subparsers(dest="voice_command", metavar="<make|upload>")

    m = vsub.add_parser("make", help="recording -> voices/<Name>/clone.wav, ref_0..2", description=__doc__,
                        formatter_class=argparse.RawDescriptionHelpFormatter)
    m.add_argument("--name", required=True, help="voice folder name, e.g. Narrator (games refer to it as Narrator/clone)")
    m.add_argument("--audio", required=True, help="the recording (any format ffmpeg reads)")
    m.add_argument("--mood", help="e.g. angry or drunk: clips for lines tagged [angry] / [drunk]")
    m.add_argument("--out", default="voices", help="where voice folders go (default: voices)")
    m.add_argument("--skip", type=float, default=0.0, help="seconds to skip at the start (an intro)")
    m.add_argument("--stt-url", help="OpenAI-compatible transcription server (base URL ending in /v1) instead of fal, "
                                     "e.g. server/asr.py's https://<workspace>--npcvoice-asr.modal.run/v1")
    m.add_argument("--stt-model", default=STT_OPEN, help=f"model name sent to --stt-url (default: {STT_OPEN})")
    m.add_argument("--stt-key-env", default="NPCVOICE_TOKEN",
                   help="environment variable with --stt-url's bearer token, if it needs one (default: NPCVOICE_TOKEN)")
    m.set_defaults(run=run_make)

    u = vsub.add_parser("upload", help="voices/<Name> -> the server's Modal volume", description=__doc__,
                        formatter_class=argparse.RawDescriptionHelpFormatter)
    u.add_argument("folder", help="a voice folder made by `voice make`, e.g. voices/Narrator")
    u.add_argument("--volume", default="npcvoice-models", help="Modal volume the server reads (default: npcvoice-models)")
    u.add_argument("--dry-run", action="store_true", help="print the modal command instead of running it")
    u.set_defaults(run=run_upload)



# ------------------------------------------------------------------ analysis

def decode(path, rate=SR):
    """Mono 16-bit PCM at `rate` as a numpy array (ffmpeg does the decoding)."""
    import numpy as np

    out = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(rate), "-f", "s16le", "-"],
                         capture_output=True, check=True)
    return np.frombuffer(out.stdout, dtype=np.int16)


def find_lines(pcm, skip=0.0):
    """Split speech into lines at pauses of SPLIT_SILENCE, with each line's loudness and peak."""
    import numpy as np

    frames = len(pcm) // FRAME
    x = pcm[: frames * FRAME].astype(np.float64).reshape(frames, FRAME)
    db = 10 * np.log10((x * x).mean(axis=1) / (32768.0 * 32768.0) + 1e-12)
    quiet = round(SPLIT_SILENCE * 50)
    lines, start, last = [], -1, -1
    for f in range(frames + 1):
        loud = f < frames and db[f] > SPEECH_DB
        if loud:
            if start < 0:
                start = f
            last = f
        elif start >= 0 and (f - last >= quiet or f == frames):
            s, e = start / 50, (last + 1) / 50
            if e - s >= 0.4:
                seg = pcm[start * FRAME:(last + 1) * FRAME].astype(np.float64)
                rms = 10 * np.log10((seg * seg).mean() / (32768.0 * 32768.0) + 1e-12)
                lines.append(Line(s, e, float(rms), float(np.abs(seg).max() / 32768.0)))
            start = -1
    return [l for l in lines if l.start >= skip]


def candidates(lines, mood=None, want=12):
    """Runs of neighbouring lines, 6-11 s once joined, scored for clean, even, normal-level speech; the best
    `want` that don't overlap."""
    scored = []
    for i in range(len(lines)):
        run, dur = [], 0.0
        for j in range(i, min(len(lines), i + 4)):
            d = lines[j].end - lines[j].start + 2 * PAD + (GAP if run else 0)
            if dur + d > MAX_REF:
                break
            run.append(lines[j])
            dur += d
            if dur < MIN_REF:
                continue
            loudest, quietest = max(l.rms for l in run), min(l.rms for l in run)
            clipped = any(l.peak > 0.98 for l in run)
            score = (-abs(loudest - (-15 if mood == "angry" else -20)) * 0.4   # normal speech (shouting for angry)
                     - (loudest - quietest) * 0.3                               # lines at similar loudness
                     - (5 if clipped else 0) - (len(run) - 1) * 0.7 - abs(dur - 9) * 0.3)
            if not mood and loudest > -13:
                score -= 4                                                      # shouting isn't a neutral reference
            scored.append(Candidate(list(run), dur, score))
    scored.sort(key=lambda c: -c.score)
    picked = []
    for c in scored:
        if not any(c.lines[0].start < p.lines[-1].end + 5 and p.lines[0].start < c.lines[-1].end + 5 for p in picked):
            picked.append(c)
        if len(picked) >= want:
            break
    return picked


def judge(c):
    """Transcript sanity: too few words or an odd speaking rate means screams, grunts or another language."""
    words = sum(1 for w in c.text.split() if any(ch.isalpha() for ch in w))
    wps = words / c.dur if c.dur else 0
    c.words = words
    if words < 6 or wps < 1.4 or wps > 4.8:
        c.score -= 100
    else:
        c.score += 2 - abs(wps - 2.8)
    return c


# ------------------------------------------------------------------ audio files

def cut(src, lines, out):
    """The lines of one candidate, joined with short pauses, loudness-normalised, 44.1 kHz mono."""
    inputs, chains = [], []
    for i, l in enumerate(lines):
        inputs += ["-ss", f"{max(0.0, l.start - PAD):.3f}", "-t", f"{l.end - l.start + 2 * PAD:.3f}", "-i", str(src)]
        pad = f",apad=pad_dur={GAP}" if i < len(lines) - 1 else ""
        chains.append(f"[{i}:a]afade=t=in:d=0.02,areverse,afade=t=in:d=0.03,areverse{pad}[a{i}]")
    graph = ";".join(chains) + ";" + "".join(f"[a{i}]" for i in range(len(lines))) + \
        f"concat=n={len(lines)}:v=0:a=1,loudnorm=I=-20:TP=-1.5:LRA=11,aresample=44100[out]"
    subprocess.run(["ffmpeg", "-v", "error", "-y", *inputs, "-filter_complex", graph, "-map", "[out]",
                    "-ac", "1", "-ar", "44100", "-c:a", "pcm_s16le", str(out)], check=True)


def join(wavs, out):
    inputs = [a for w in wavs for a in ("-i", str(w))]
    graph = ";".join(f"[{i}:a]apad=pad_dur=0.3[a{i}]" for i in range(len(wavs))) + ";" + \
        "".join(f"[a{i}]" for i in range(len(wavs))) + f"concat=n={len(wavs)}:v=0:a=1[out]"
    subprocess.run(["ffmpeg", "-v", "error", "-y", *inputs, "-filter_complex", graph, "-map", "[out]",
                    "-ac", "1", "-ar", "44100", "-c:a", "pcm_s16le", str(out)], check=True)


def transcription_request(wav, url, model, key):
    """POST <url>/audio/transcriptions as OpenAI's API: multipart file + model, answers {"text"}."""
    boundary = "npctalk" + os.urandom(8).hex()
    parts = []
    for name, value in (("model", model), ("response_format", "json")):
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{Path(wav).name}"\r\n'
                 f'Content-Type: audio/wav\r\n\r\n'.encode() + Path(wav).read_bytes() + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    headers = {"Content-Type": f"multipart/form-data; boundary={boundary}"}
    if key:
        headers["Authorization"] = f"Bearer {key}"
    return urllib.request.Request(url.rstrip("/") + "/audio/transcriptions", data=b"".join(parts), headers=headers, method="POST")


def transcribe(wav, key, url=None, model=STT_OPEN):
    """fal's Nemotron endpoint, or with url any OpenAI-compatible transcription server."""
    if url:
        req = transcription_request(wav, url, model, key)
    else:
        audio = base64.b64encode(Path(wav).read_bytes()).decode()
        req = urllib.request.Request(f"https://fal.run/{STT}", method="POST",
                                     data=json.dumps({"audio_url": f"data:audio/wav;base64,{audio}"}).encode(),
                                     headers={"Authorization": f"Key {key}", "Content-Type": "application/json"})
    last = None
    for _ in range(3):
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                body = json.loads(r.read())
            return str(body.get("output") or body.get("text") or "").strip()
        except OSError as e:
            last = e
    raise RuntimeError(f"speech-to-text failed: {last}")


# ------------------------------------------------------------------ commands

def run_make(args):
    if args.stt_url:
        key = os.environ.get(args.stt_key_env, "")
    else:
        key = os.environ.get("FAL_KEY", "")
        if not key:
            sys.exit("set FAL_KEY: the candidates are transcribed with fal speech-to-text (https://fal.ai/dashboard/keys),\n"
                     "or transcribe on your own server with --stt-url (server/asr.py runs NVIDIA Nemotron ASR's open weights)")
    if not shutil.which("ffmpeg"):
        sys.exit("ffmpeg isn't on PATH")
    lines = find_lines(decode(args.audio), args.skip)
    cands = candidates(lines, args.mood, 6 if args.mood else 12)
    label = args.name + (f"/{args.mood}" if args.mood else "")
    print(f"{label}: {len(lines)} lines, {len(cands)} candidates")
    if not cands:
        print("no 6-11 s runs of speech found: is it one speaker at normal level, with pauses between lines?")
        return 1

    work = Path("voices_work") / (args.name + (f"_{args.mood}" if args.mood else ""))
    work.mkdir(parents=True, exist_ok=True)

    def prepare(item):
        i, c = item
        c.wav = str(work / f"cand_{i:02d}.wav")
        cut(args.audio, c.lines, c.wav)
        c.text = transcribe(c.wav, key, args.stt_url, args.stt_model)
        Path(c.wav).with_suffix(".lab").write_text(c.text, encoding="utf-8")
        return judge(c)

    with ThreadPoolExecutor(6) as pool:
        for c in pool.map(prepare, enumerate(cands)):
            print(f"  {Path(c.wav).name} {c.dur:.1f}s score {c.score:.1f} | {c.text}")

    good = sorted((c for c in cands if c.score > -50), key=lambda c: -c.score)
    if not good:
        print("no usable clips (all screams, noise or another language?)")
        return 1
    out = Path(args.out) / args.name / (args.mood or "")
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("*"):
        if old.is_file() and old.stem.startswith(("ref_", "clone")) and old.suffix in (".wav", ".lab"):
            old.unlink()
    refs = good[: 2 if args.mood else 3]
    for i, c in enumerate(refs):
        shutil.copyfile(c.wav, out / f"ref_{i}.wav")
        (out / f"ref_{i}.lab").write_text(c.text, encoding="utf-8")
    if args.mood:
        print(f"-> {out}: {len(refs)} refs")
        return 0
    chosen, total = [], 0.0
    for c in good:
        if total >= CLONE_SECONDS:
            break
        chosen.append(c)
        total += c.dur + 0.3
    join([c.wav for c in chosen], out / "clone.wav")
    (out / "clone.lab").write_text(" ".join(c.text for c in chosen), encoding="utf-8")
    print(f"-> {out}: {len(refs)} refs, clone.wav {total:.1f}s from {len(chosen)} clips")
    return 0


def run_upload(args):
    folder = Path(args.folder)
    if not (folder / "clone.wav").exists() and not any(folder.glob("ref_*.wav")):
        sys.exit(f"{folder}: no clone.wav or ref_*.wav (make it with `npctalk voice make`)")
    modal = shutil.which("modal")
    cmd = [modal or "modal", "volume", "put", "--force", args.volume, str(folder), f"/voices/{folder.name}"]
    if args.dry_run or not modal:
        print(" ".join(cmd) + ("" if modal else "   # modal isn't on PATH: install it (uv tool install modal)"))
        return 0 if args.dry_run else 1
    subprocess.run(cmd, check=True)
    print(f"uploaded. Restart the server so it encodes the new voice: modal app stop npcvoice-drama")
    return 0
