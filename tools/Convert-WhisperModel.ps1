<#
.SYNOPSIS
    Converts a Whisper model in Hugging Face (transformers) format, such as an ATC fine-tune or your own, to the
    whisper.cpp (GGML) format the ATC app uses, and quantizes it.

.DESCRIPTION
    Everything runs locally on the CPU: a Python environment with PyTorch (CPU build) is made once in
    out/whisper-convert/, the model is downloaded there, converted with whisper.cpp's converter and quantized with
    whisper.cpp's quantize tool (official release). The result goes to data/voice/models/<Name>.bin.
    Needs Python 3.10+ and about 3x the model's size in free disk space while it runs.

.EXAMPLE
    .\tools\Convert-WhisperModel.ps1 -Repo tclin/whisper-large-v3-turbo-atcosim-finetune -Name ggml-turbo-atcosim-q5_0

.EXAMPLE
    .\tools\Convert-WhisperModel.ps1 -Path D:\training\merged-model -Name ggml-turbo-digii-q5_0
#>
param(
    # A Hugging Face model id ...
    [string]$Repo,
    # ... or a local model folder (config.json, vocab.json, model.safetensors).
    [string]$Path,
    # Output: data/voice/models/<Name>.bin
    [Parameter(Mandatory = $true)][string]$Name,
    [ValidateSet('f16', 'q8_0', 'q5_0', 'q5_1', 'q4_0')][string]$Quantize = 'q5_0',
    # Where the Python environment and downloads live (default: out/whisper-convert).
    [string]$Work
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell does not set $PSScriptRoot in parameter defaults.
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Work) { $Work = Join-Path $Root '..\out\whisper-convert' }
# Pinned versions: whisper.cpp converter and quantize tool, OpenAI's mel filters, the Python packages.
$WhisperCppTag = 'v1.9.4'
$WhisperCppBuild = 'b5130'
$OpenAiWhisperTag = 'v20240930'
$PythonPackages = @('torch==2.8.0', 'transformers==4.56.2', 'numpy<2.4')

if (-not $Repo -and -not $Path) { throw 'Give -Repo <huggingface id> or -Path <model folder>.' }
$Work = [IO.Path]::GetFullPath($Work)
$Models = [IO.Path]::GetFullPath((Join-Path $Root '..\data\voice\models'))
New-Item -ItemType Directory -Force $Work, $Models | Out-Null
$Target = Join-Path $Models "$Name.bin"
if (Test-Path $Target) { Write-Host "$Target already exists."; return }

# GitHub and Hugging Face need TLS 1.2, which Windows PowerShell does not always offer by itself.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# Downloads retry: connections to GitHub and Hugging Face are sometimes reset.
function Get-File([string]$Url, [string]$OutFile) {
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing $Url -OutFile "$OutFile.part"
            Move-Item -Force "$OutFile.part" $OutFile
            return
        }
        catch {
            if ($attempt -ge 5) { throw }
            Write-Host "  download failed ($($_.Exception.Message)), retrying"
            Start-Sleep -Seconds (3 * $attempt)
        }
    }
}

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed (exit $LASTEXITCODE)" }
}

# 1. Python environment (once).
$Venv = Join-Path $Work 'venv'
$Python = Join-Path $Venv 'Scripts\python.exe'
if (-not (Test-Path $Python)) {
    Write-Host 'Setting up Python with PyTorch (CPU) in' $Venv
    $System = if (Get-Command py -ErrorAction SilentlyContinue) { 'py' } else { 'python' }
    Invoke-Checked $System @('-m', 'venv', $Venv)
    Invoke-Checked $Python @('-m', 'pip', 'install', '--quiet', '--upgrade', 'pip')
    Invoke-Checked $Python (@('-m', 'pip', 'install', '--quiet', '--extra-index-url', 'https://download.pytorch.org/whl/cpu') + $PythonPackages)
}

# 2. The model.
if ($Repo) {
    $ModelDir = Join-Path $Work ('hf\' + ($Repo -replace '/', '__'))
    Write-Host "Downloading $Repo"
    Invoke-Checked $Python @('-I', '-c', "from huggingface_hub import snapshot_download; snapshot_download('$Repo', local_dir=r'$ModelDir', allow_patterns=['*.json', '*.txt', 'model*.safetensors'])")
}
else {
    $ModelDir = [IO.Path]::GetFullPath($Path)
}
# The converter reads added_tokens.json, which some fine-tunes leave out (it is not used for the conversion).
$Added = Join-Path $ModelDir 'added_tokens.json'
if (-not (Test-Path $Added)) { Set-Content -Path $Added -Value '{}' -Encoding ascii }

# 3. whisper.cpp's converter and OpenAI's mel filters.
$Converter = Join-Path $Work "convert-h5-to-ggml-$WhisperCppTag.py"
if (-not (Test-Path $Converter)) {
    Get-File "https://raw.githubusercontent.com/ggml-org/whisper.cpp/$WhisperCppTag/models/convert-h5-to-ggml.py" $Converter
}
$OpenAiWhisper = Join-Path $Work "openai-whisper-$OpenAiWhisperTag"
$MelFilters = Join-Path $OpenAiWhisper 'whisper\assets\mel_filters.npz'
if (-not (Test-Path $MelFilters)) {
    New-Item -ItemType Directory -Force (Split-Path $MelFilters) | Out-Null
    Get-File "https://raw.githubusercontent.com/openai/whisper/$OpenAiWhisperTag/whisper/assets/mel_filters.npz" $MelFilters
}

# 4. Convert (16-bit); a conversion left by an interrupted run is used again.
$Out = Join-Path $Work "out-$Name"
$F16 = Join-Path $Out 'ggml-model.bin'
if (-not (Test-Path $F16)) {
    New-Item -ItemType Directory -Force $Out | Out-Null
    Write-Host 'Converting to GGML'
    Invoke-Checked $Python @('-I', $Converter, $ModelDir, $OpenAiWhisper, $Out) | Where-Object { $_ -notmatch '->|Converting to float32|^\S+ \d+ \(' }
}

# 5. Quantize with whisper.cpp's tool (official Windows release).
if ($Quantize -eq 'f16') {
    Move-Item $F16 $Target
}
else {
    $Bin = Join-Path $Work "whisper-bin-$WhisperCppBuild"
    $Quantizer = Join-Path $Bin 'Release\whisper-quantize.exe'
    if (-not (Test-Path $Quantizer)) {
        $Zip = Join-Path $Work "whisper-bin-x64-$WhisperCppBuild.zip"
        Get-File "https://github.com/ggml-org/whisper.cpp/releases/download/$WhisperCppBuild/whisper-bin-x64.zip" $Zip
        Expand-Archive $Zip -DestinationPath $Bin -Force
    }
    Write-Host "Quantizing to $Quantize"
    Invoke-Checked $Quantizer @($F16, $Target, $Quantize) | Select-Object -Last 3
    Remove-Item $F16
}
Write-Host ("Ready: {0} ({1:N0} MB). Add it to data/voice/recognition.json to use it." -f $Target, ((Get-Item $Target).Length / 1MB))
