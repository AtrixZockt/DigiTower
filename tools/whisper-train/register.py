"""Makes a tuned Whisper model a recognizer DigiTower offers: two entries (with and without the vocabulary prompt, on
the GPU) in %APPDATA%\\DigiTower\\recognition.local.json, beside the settings, so updates of DigiTower keep them, and
the model in both model folders (the repository's data\\voice\\models and an installed DigiTower's
%LOCALAPPDATA%\\DigiTower\\models). Run by tools/Train-Whisper.ps1."""
import argparse
import json
import os
import re
import shutil
from pathlib import Path


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--model', required=True, help='the converted model, e.g. data\\voice\\models\\ggml-turbo-tuned-q5_0.bin')
    p.add_argument('--id', default='whisper-turbo-tuned', help='recognizer id prefix')
    p.add_argument('--label', default='Whisper turbo tuned on my voice')
    args = p.parse_args()

    model = Path(args.model).resolve()
    name = model.stem
    installed = Path(os.environ['LOCALAPPDATA']) / 'DigiTower' / 'models' / model.name
    installed.parent.mkdir(parents=True, exist_ok=True)
    if installed != model:
        shutil.copyfile(model, installed)
        print(f'copied to {installed}')

    user = Path(os.environ['APPDATA']) / 'DigiTower' / 'recognition.local.json'
    user.parent.mkdir(parents=True, exist_ok=True)
    data = {}
    if user.exists():
        # The file may carry // comments (DigiTower reads them); they are not kept.
        text = re.sub(r'^\s*//.*$', '', user.read_text(encoding='utf-8'), flags=re.M)
        data = json.loads(text) if text.strip() else {}
    models = [m for m in data.get('models', []) if m.get('name') != name]
    models.append({'name': name, 'license': 'your own fine-tune of OpenAI Whisper (MIT)', 'sizeMb': round(model.stat().st_size / 1e6)})
    ids = {f'{args.id}-vulkan', f'{args.id}-plain-vulkan'}
    recognizers = [r for r in data.get('recognizers', []) if r.get('id') not in ids]
    recognizers.append({'id': f'{args.id}-vulkan', 'label': f'{args.label}, prompt (GPU, Vulkan)', 'engine': 'whisper',
                        'model': name, 'runtime': 'vulkan'})
    recognizers.append({'id': f'{args.id}-plain-vulkan', 'label': f'{args.label} (GPU, Vulkan)', 'engine': 'whisper',
                        'model': name, 'runtime': 'vulkan', 'vocabulary': False})
    data['models'], data['recognizers'] = models, recognizers
    user.write_text(json.dumps(data, indent=2), encoding='utf-8')
    print(f'registered {args.id}-vulkan and {args.id}-plain-vulkan in {user}')


if __name__ == '__main__':
    main()
