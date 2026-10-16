"""NVIDIA Nemotron ASR on Modal behind an OpenAI-compatible transcription API: the player's speech to text on open
weights (nvidia/nemotron-3.5-asr-streaming-0.6b, OpenMDW-1.1, 40 language-locales), on your own GPU instead of
the fal endpoint.

    modal deploy server/asr.py      # https://<workspace>--npcvoice-asr.modal.run

Endpoints:
    POST /v1/audio/transcriptions   multipart, as OpenAI's API: file (any format ffmpeg reads), model (ignored),
                                    language ("en", "de", "en-US", ... or "auto", the default), response_format
                                    ("json" or "text") -> {"text": "..."}. The mod's [Stt] Provider = openai and
                                    `npctalk voice make --stt-url` call it with base URL <app url>/v1.
    GET  /health                    no auth.

Auth: "Authorization: Bearer <NPCVOICE_TOKEN>" from the npcvoice-auth secret, like the voice servers.

A 0.6B model, so an L4 is plenty (about $0.80/hour on Modal while it runs). Weights are baked into the image, so a
cold start is the container plus loading the model; the GPU stops SCALEDOWN seconds after the last request. The
mod's keep-warm pings only the voice server, so the first sentence after a quiet spell waits for this one to start.
"""
import hmac
import os
import subprocess
import threading

import modal

APP_NAME = "npcvoice-asr"
MODEL = "nvidia/nemotron-3.5-asr-streaming-0.6b"
SCALEDOWN = 300

image = (
    modal.Image.debian_slim(python_version="3.12")
    .apt_install("ffmpeg")
    .pip_install("torch", "transformers>=5.13.0", "accelerate", "sentencepiece", "librosa", "numpy", "fastapi[standard]",
                 "python-multipart", "huggingface_hub")
    # the Transformers checkpoint only, not the repo's NeMo and GGUF copies
    .run_commands(f"python -c \"from huggingface_hub import snapshot_download; "
                  f"snapshot_download('{MODEL}', ignore_patterns=['*.nemo', '*.gguf'])\"")
)

app = modal.App(APP_NAME)


@app.cls(
    image=image,
    gpu="L4",
    secrets=[modal.Secret.from_name("npcvoice-auth")],
    scaledown_window=SCALEDOWN,
    timeout=600,
)
@modal.concurrent(max_inputs=8)
class Asr:
    @modal.enter()
    def load(self):
        from transformers import AutoModelForRNNT, AutoProcessor

        self.processor = AutoProcessor.from_pretrained(MODEL)
        self.model = AutoModelForRNNT.from_pretrained(MODEL, device_map="cuda")
        self.rate = self.processor.feature_extractor.sampling_rate
        self.lock = threading.Lock()  # one generate at a time on the GPU; short clips take tens of ms

    def transcribe(self, audio: bytes, language: str) -> str:
        import numpy as np
        import torch

        pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", "pipe:0", "-ac", "1", "-ar", str(self.rate), "-f", "f32le", "pipe:1"],
                             input=audio, capture_output=True, check=True).stdout
        samples = np.frombuffer(pcm, dtype=np.float32)
        if samples.size < self.rate // 10:
            return ""
        inputs = self.processor(samples, sampling_rate=self.rate, language=language)
        inputs = inputs.to(self.model.device, dtype=self.model.dtype)
        with self.lock, torch.inference_mode():
            out = self.model.generate(**inputs, return_dict_in_generate=True)
        text = self.processor.decode(out.sequences, skip_special_tokens=True)
        return (text[0] if isinstance(text, list) else text).strip()

    @modal.asgi_app(label=APP_NAME)
    def web(self):
        from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile
        from fastapi.responses import PlainTextResponse

        api = FastAPI()
        token = os.environ["NPCVOICE_TOKEN"]

        @api.get("/health")
        def health():
            return {"ok": True, "model": MODEL}

        @api.post("/v1/audio/transcriptions")
        def transcriptions(file: UploadFile = File(...), model: str = Form(MODEL), language: str = Form("auto"),
                           response_format: str = Form("json"), authorization: str = Header("")):
            if not hmac.compare_digest(authorization.encode(), f"Bearer {token}".encode()):
                raise HTTPException(401, "bad token")
            try:
                text = self.transcribe(file.file.read(), language or "auto")
            except subprocess.CalledProcessError as e:
                raise HTTPException(400, f"couldn't decode the audio: {e.stderr.decode(errors='replace')[:200]}")
            return PlainTextResponse(text) if response_format == "text" else {"text": text}

        return api
