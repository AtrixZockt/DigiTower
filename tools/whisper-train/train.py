"""Tunes Whisper large-v3-turbo on a pilot's own radio calls with LoRA, for DigiTower's voice requests.

Run by tools/Train-Whisper.ps1, which sets up the Python environment, converts the result for whisper.cpp and compares
it with the stock model. Training data:
  * recorded practice calls: VoiceTool record --calls data/voice/train-calls.txt --out out/train/calls (calls.json),
  * calls heard in flight, checked with VoiceTool review: out/train/flight/labels.jsonl (skipped ones left out).
The bake-off's test calls (out/s5/calls) are never used here, so they stay a fair test.

Only small low-rank adapters (LoRA) on the attention layers are trained; the original weights stay as they are and
the adapters are merged into a copy at the end (out/.../merged, Hugging Face format).
"""
import argparse
import json
import math
import os
import random
import re
import sys
import time
import wave
from pathlib import Path

import numpy as np

SAMPLE_RATE = 16000


def log(text):
    print(f'{time.strftime("%H:%M:%S")}  {text}', flush=True)


# ---- data ------------------------------------------------------------------------------------------------------

def load_wav(path):
    """A WAV file (16-bit PCM) as float32 mono at 16 kHz."""
    with wave.open(str(path), 'rb') as w:
        channels, width, rate, frames = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(frames)
    if width != 2:
        raise ValueError(f'{path}: {8 * width}-bit WAV, expected 16-bit')
    audio = np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768.0
    if channels > 1:
        audio = audio.reshape(-1, channels).mean(axis=1)
    if rate != SAMPLE_RATE:
        audio = resample(audio, rate / SAMPLE_RATE)
    return audio


def resample(audio, factor):
    """Plays the audio `factor` times faster (factor > 1: shorter)."""
    n = max(1, int(round(len(audio) / factor)))
    return np.interp(np.linspace(0, len(audio) - 1, n), np.arange(len(audio)), audio).astype(np.float32)


def clean(text):
    """The call as Whisper should write it: no [key] brackets, single spaces."""
    return re.sub(r'\s+', ' ', re.sub(r'[\[\]]', '', text)).strip()


def gather(call_dirs, flight_labels):
    items = []
    for d in call_dirs:
        manifest = Path(d) / 'calls.json'
        if not manifest.exists():
            log(f'no recordings in {d} (no calls.json)')
            continue
        for c in json.loads(manifest.read_text(encoding='utf-8')):
            path = Path(d) / c['file']
            if path.exists():
                items.append((path, clean(c['text']), 'recorded'))
    if flight_labels and Path(flight_labels).exists():
        for line in Path(flight_labels).read_text(encoding='utf-8').splitlines():
            if not line.strip():
                continue
            c = json.loads(line)
            if not c.get('skip') and Path(c['file']).exists() and c.get('text'):
                items.append((Path(c['file']), clean(c['text']), 'flight'))
    return items


def augment(audio, rng):
    """A little variety each epoch: speed ±8 %, level ±6 dB, a faint hiss."""
    audio = resample(audio, rng.uniform(0.92, 1.08))
    audio = audio * (10 ** (rng.uniform(-6, 6) / 20))
    noise = rng.standard_normal(len(audio)).astype(np.float32) * (10 ** (rng.uniform(-50, -38) / 20))
    return np.clip(audio + noise, -1, 1).astype(np.float32)


# ---- training --------------------------------------------------------------------------------------------------

def pick_device(torch, wanted):
    if wanted != 'cpu' and torch.cuda.is_available():
        # The card with the most memory (with two GPUs, the big one).
        best = max(range(torch.cuda.device_count()), key=lambda i: torch.cuda.get_device_properties(i).total_memory)
        props = torch.cuda.get_device_properties(best)
        log(f'training on the GPU: {props.name}, {props.total_memory / 2**30:.0f} GB')
        return torch.device(f'cuda:{best}')
    if wanted == 'cuda':
        raise SystemExit('no usable GPU (torch.cuda.is_available() is False)')
    threads = max(1, (os.cpu_count() or 2) // 2)
    torch.set_num_threads(threads)
    log(f'training on the CPU ({threads} threads): this takes hours')
    return torch.device('cpu')


def from_pretrained(cls, name, **kwargs):
    """Downloads retry: connections to Hugging Face are sometimes reset."""
    for attempt in range(1, 8):
        try:
            return cls.from_pretrained(name, **kwargs)
        except OSError as e:
            if attempt == 7:
                raise
            log(f'download failed ({e}), retrying')
            time.sleep(5 * attempt)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--calls', nargs='*', default=['out/train/calls'], help='folders of recorded practice calls (calls.json)')
    p.add_argument('--flight', default='out/train/flight/labels.jsonl', help='checked flight calls (VoiceTool review)')
    p.add_argument('--model', default='openai/whisper-large-v3-turbo')
    p.add_argument('--out', default='out/whisper-train/run')
    p.add_argument('--device', choices=['auto', 'cpu', 'cuda'], default='auto')
    p.add_argument('--epochs', type=int, default=6)
    p.add_argument('--lr', type=float, default=3e-4)
    p.add_argument('--batch', type=int, default=2)
    p.add_argument('--accumulate', type=int, default=4, help='batches per optimizer step')
    p.add_argument('--rank', type=int, default=32, help='LoRA rank')
    p.add_argument('--seed', type=int, default=1)
    p.add_argument('--hours', type=float, default=9, help='stop after this long (keeps the best so far)')
    p.add_argument('--smoke', action='store_true', help='a 1-minute check of the whole pipeline with whisper-tiny')
    args = p.parse_args()

    import torch
    from peft import LoraConfig, PeftModel, get_peft_model
    from transformers import WhisperForConditionalGeneration, WhisperProcessor

    start = time.time()
    rng = np.random.default_rng(args.seed)
    random.seed(args.seed)
    torch.manual_seed(args.seed)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    items = gather(args.calls, args.flight)
    if args.smoke:
        args.model, args.epochs, args.batch, args.accumulate = 'openai/whisper-tiny', 1, 2, 1
        items = items[:6]
    recorded = sum(1 for i in items if i[2] == 'recorded')
    log(f'{len(items)} calls to learn from: {recorded} recorded, {len(items) - recorded} from flights')
    if len(items) < (4 if args.smoke else 30):
        raise SystemExit('too few calls: record the practice calls (VoiceTool record --calls data/voice/train-calls.txt '
                         '--out out/train/calls) and check the flight calls (VoiceTool review) first')

    # A few calls are kept aside to see whether it learns or only memorizes.
    random.shuffle(items)
    n_val = 2 if args.smoke else max(5, len(items) // 10)
    val, train = items[:n_val], items[n_val:]
    audio = {path: load_wav(path) for path, _, _ in items}

    device = pick_device(torch, args.device)
    cuda = device.type == 'cuda'
    dtype = torch.bfloat16 if cuda and torch.cuda.is_bf16_supported() else torch.float16 if cuda else torch.float32
    processor = from_pretrained(WhisperProcessor, args.model)
    processor.tokenizer.set_prefix_tokens(language='en', task='transcribe', predict_timestamps=False)
    model = from_pretrained(WhisperForConditionalGeneration, args.model)
    model.config.forced_decoder_ids = None
    model.generation_config.forced_decoder_ids = None
    # SpecAugment: random masks in time and frequency while training, against learning the recordings by heart.
    model.config.apply_spec_augment = True
    model.config.mask_time_prob = 0.05
    model.config.mask_feature_prob = 0.05
    lora = LoraConfig(r=args.rank, lora_alpha=2 * args.rank, lora_dropout=0.05, bias='none',
                      target_modules=['q_proj', 'k_proj', 'v_proj', 'out_proj'])
    model = get_peft_model(model, lora)
    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    log(f'model {args.model}: {trainable / 1e6:.1f} M trainable parameters (LoRA rank {args.rank})')
    model.to(device)

    start_token = model.config.decoder_start_token_id

    def batch_of(chunk, train_mode):
        clips = [augment(audio[path], rng) if train_mode else audio[path] for path, _, _ in chunk]
        features = processor.feature_extractor(clips, sampling_rate=SAMPLE_RATE, return_tensors='pt').input_features
        labels = [processor.tokenizer(text).input_ids for _, text, _ in chunk]
        width = max(len(ids) for ids in labels)
        padded = torch.full((len(labels), width), -100, dtype=torch.long)
        for i, ids in enumerate(labels):
            padded[i, :len(ids)] = torch.tensor(ids)
        if (padded[:, 0] == start_token).all():
            padded = padded[:, 1:]
        return features.to(device), padded.to(device)

    steps_per_epoch = math.ceil(len(train) / (args.batch * args.accumulate))
    total_steps = steps_per_epoch * args.epochs
    warmup = max(1, total_steps // 10)
    optimizer = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad], lr=args.lr, weight_decay=0.01)
    scheduler = torch.optim.lr_scheduler.LambdaLR(
        optimizer, lambda s: (s + 1) / warmup if s < warmup else max(0.05, (total_steps - s) / max(1, total_steps - warmup)))
    scaler = torch.amp.GradScaler('cuda') if cuda and dtype == torch.float16 else None
    log(f'{len(train)} calls to train on, {len(val)} to check against; {args.epochs} epochs of {steps_per_epoch} steps')

    def loss_of(features, labels):
        with torch.autocast(device_type=device.type, dtype=dtype, enabled=cuda):
            return model(input_features=features, labels=labels).loss

    def validate():
        model.eval()
        losses, right = [], 0
        with torch.no_grad():
            for i in range(0, len(val), args.batch):
                chunk = val[i:i + args.batch]
                features, labels = batch_of(chunk, False)
                losses.append(loss_of(features, labels).item())
                with torch.autocast(device_type=device.type, dtype=dtype, enabled=cuda):
                    ids = model.generate(input_features=features, max_new_tokens=96, language='en', task='transcribe')
                for (_, text, _), said in zip(chunk, processor.batch_decode(ids, skip_special_tokens=True)):
                    norm = lambda s: re.sub(r'[^a-z0-9]+', ' ', s.lower()).strip()
                    right += norm(said) == norm(text)
        model.train()
        return sum(losses) / len(losses), right

    best = None
    loss0, right0 = validate()
    log(f'before training: check loss {loss0:.3f}, {right0}/{len(val)} check calls written exactly right')
    model.train()
    step, timed = 0, time.time()
    for epoch in range(1, args.epochs + 1):
        random.shuffle(train)
        batches = [train[i:i + args.batch] for i in range(0, len(train), args.batch)]
        for b, chunk in enumerate(batches):
            features, labels = batch_of(chunk, True)
            loss = loss_of(features, labels) / args.accumulate
            (scaler.scale(loss) if scaler else loss).backward()
            if (b + 1) % args.accumulate == 0 or b + 1 == len(batches):
                if scaler:
                    scaler.step(optimizer)
                    scaler.update()
                else:
                    optimizer.step()
                optimizer.zero_grad(set_to_none=True)
                scheduler.step()
                step += 1
                if step == 3 or step % 20 == 0:
                    per_step = (time.time() - timed) / step
                    log(f'step {step}/{total_steps}, loss {loss.item() * args.accumulate:.3f}, '
                        f'{per_step:.1f} s per step, about {(total_steps - step) * per_step / 60:.0f} min to go')
                if args.smoke and step >= 2:
                    break
        val_loss, right = validate()
        log(f'epoch {epoch}/{args.epochs}: check loss {val_loss:.3f}, {right}/{len(val)} check calls exactly right')
        if best is None or val_loss < best:
            best = val_loss
            model.save_pretrained(out / 'adapter')
            log('  best so far: adapter saved')
        if time.time() - start > args.hours * 3600:
            log('time is up: keeping the best so far')
            break

    # The adapters merged into a copy of the model, in Hugging Face format for the converter.
    log('merging the adapters into the model')
    base = from_pretrained(WhisperForConditionalGeneration, args.model, torch_dtype=torch.float16)
    merged = PeftModel.from_pretrained(base, out / 'adapter').merge_and_unload()
    merged.generation_config.forced_decoder_ids = None
    merged.save_pretrained(out / 'merged', safe_serialization=True)
    processor.save_pretrained(out / 'merged')
    summary = {'model': args.model, 'calls': len(items), 'train': len(train), 'check': len(val),
               'before': {'loss': loss0, 'right': right0}, 'best_loss': best, 'minutes': (time.time() - start) / 60,
               'device': str(device)}
    (out / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    log(f'done in {(time.time() - start) / 60:.0f} min: {out / "merged"}')


if __name__ == '__main__':
    main()
