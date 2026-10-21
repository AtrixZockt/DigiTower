<#
.SYNOPSIS
    Tunes the speech recognizer (Whisper large-v3-turbo) on your own voice overnight, and compares it with the stock
    model on the test calls.

.DESCRIPTION
    Before: record the practice calls and check the calls heard in flight (docs/voice.md, "Tuning the recognizer on
    your voice"):
        dotnet run --project tools/DcsAtc.VoiceTool -- record --calls data/voice/train-calls.txt --out out/train/calls
        dotnet run --project tools/DcsAtc.VoiceTool -- review

    Then this script, which runs unattended:
      1. A Python environment of its own in out\whisper-train\venv (once): AMD's PyTorch for Windows for the
         graphics card if it works there, else the CPU build. Nothing is installed system-wide, drivers are not touched.
      2. A one-minute check of everything with a tiny model (-SkipCheck leaves it out).
      3. The training (tools\whisper-train\train.py): small LoRA adapters on your recordings, about an hour on
         the GPU, some hours on the CPU; never the test calls (out\s5\calls).
      4. The result converted for DigiTower (tools\Convert-WhisperModel.ps1): data\voice\models\<Name>.bin.
      5. Registered for you (tools\whisper-train\register.py): "Whisper turbo tuned on my voice" in Settings, in
         %APPDATA%\DigiTower\recognition.local.json; the model also copied for an installed DigiTower.
      6. The bake-off on the test calls, stock against tuned: out\train\bakeoff\report.html.
    Everything it says goes to out\whisper-train\train-<date>.log too.

.EXAMPLE
    .\tools\Train-Whisper.ps1
    .\tools\Train-Whisper.ps1 -Cpu
    .\tools\Train-Whisper.ps1 -SafeBlas      # if the GPU crashes or resets during training (an RDNA4 workaround)
#>
param(
    # Train on the CPU even if the graphics card could.
    [switch]$Cpu,
    # AMD's workaround for GPU resets during training on some Radeon RX 9000 cards (slower).
    [switch]$SafeBlas,
    [int]$Epochs = 6,
    # The converted model: data\voice\models\<Name>.bin.
    [string]$Name = 'ggml-turbo-tuned-q5_0',
    # Stop the training after this many hours (the best so far is kept).
    [double]$Hours = 9,
    [switch]$SkipCheck,
    [switch]$SkipBakeoff
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Work = Join-Path $Root 'out\whisper-train'
New-Item -ItemType Directory -Force $Work | Out-Null
$Stamp = Get-Date -Format 'yyyyMMdd-HHmm'
Start-Transcript -Path (Join-Path $Work "train-$Stamp.log") | Out-Null
Push-Location $Root
try {
    function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
    }
    function Install([string[]]$Arguments) {
        # Connections to the package servers are sometimes reset: retry.
        for ($attempt = 1; ; $attempt++) {
            & $Python -m pip install --retries 10 --timeout 60 @Arguments
            if ($LASTEXITCODE -eq 0) { return }
            if ($attempt -ge 3) { throw "pip install $($Arguments -join ' ') failed" }
            Write-Host "  pip failed, retrying"
            Start-Sleep -Seconds 10
        }
    }

    # 1. The Python environment (once). AMD's PyTorch for Windows if it sees the card, else the CPU build.
    $Venv = Join-Path $Work 'venv'
    $Python = Join-Path $Venv 'Scripts\python.exe'
    $DeviceFile = Join-Path $Work 'device.txt'
    if (-not (Test-Path $Python)) {
        Write-Host "Setting up Python in $Venv (once; several GB of downloads)"
        # Python 3.12 or 3.11 from python.org through the py launcher (the Microsoft Store builds make poor venvs).
        [string[]]$VenvArgs = @()
        $System = 'python'
        if (Get-Command py -ErrorAction SilentlyContinue) {
            $System = 'py'
            $Known = (& py -0p) -join "`n"
            foreach ($v in @('3.12', '3.11', '3.13')) {
                $line = ($Known -split "`n") | Where-Object { $_ -match "-V:$([regex]::Escape($v))\b" } | Select-Object -First 1
                if ($line -and $line -notmatch 'WindowsApps') { $VenvArgs += "-$v"; break }
            }
        }
        $VenvArgs += @('-m', 'venv', $Venv)
        Invoke-Checked $System $VenvArgs
        Invoke-Checked $Python @('-m', 'pip', 'install', '--quiet', '--upgrade', 'pip')
        Write-Host "Python: $(& $Python --version)"
        $gpu = $false
        if (-not $Cpu) {
            Write-Host 'Trying AMD PyTorch for Windows (ROCm)'
            # Warnings from pip or PyTorch are not failures here: the test below decides.
            $ErrorActionPreference = 'Continue'
            & $Python -m pip install --retries 10 --timeout 60 --index-url https://repo.amd.com/rocm/whl-multi-arch/ 'torch[device-all]'
            if ($LASTEXITCODE -eq 0) {
                & $Python -c "import torch, sys; ok = torch.cuda.is_available(); print('GPU:', torch.cuda.get_device_name(0) if ok else 'none'); sys.exit(0 if ok else 1)"
                $gpu = $LASTEXITCODE -eq 0
            }
            if (-not $gpu) {
                Write-Host 'The graphics card is not usable from PyTorch here (driver or package): training on the CPU instead.'
                & $Python -m pip uninstall --yes torch
            }
            $ErrorActionPreference = 'Stop'
        }
        if (-not $gpu) { Install @('--index-url', 'https://download.pytorch.org/whl/cpu', 'torch==2.8.0') }
        Install @('transformers==4.56.2', 'peft==0.17.1', 'numpy<2.4', 'safetensors')
        Set-Content -Path $DeviceFile -Value ($(if ($gpu) { 'cuda' } else { 'cpu' })) -Encoding ascii
    }
    $Device = if ($Cpu) { 'cpu' } elseif (Test-Path $DeviceFile) { (Get-Content $DeviceFile).Trim() } else { 'auto' }
    $env:HF_HOME = Join-Path $Work 'hf'
    $env:PYTHONUNBUFFERED = '1'
    if ($SafeBlas) { $env:TORCH_BLAS_PREFER_HIPBLASLT = '0' }
    $Train = Join-Path $Root 'tools\whisper-train\train.py'

    # 2. A quick check of everything with whisper-tiny: problems show now, not in the morning.
    if (-not $SkipCheck) {
        Write-Host 'Checking the setup with a tiny model (about a minute)'
        Invoke-Checked $Python @($Train, '--smoke', '--device', $Device, '--out', (Join-Path $Work 'check'))
    }

    # 3. The training.
    $Run = Join-Path $Work "run-$Stamp"
    Invoke-Checked $Python @($Train, '--device', $Device, '--epochs', "$Epochs", '--hours', "$Hours", '--out', $Run)

    # 4. Converted for whisper.cpp (a model of an earlier run with the same name is replaced).
    $Model = Join-Path $Root "data\voice\models\$Name.bin"
    if (Test-Path $Model) { Remove-Item $Model }
    & (Join-Path $Root 'tools\Convert-WhisperModel.ps1') -Path (Join-Path $Run 'merged') -Name $Name

    # 5. Registered as a recognizer.
    Invoke-Checked $Python @((Join-Path $Root 'tools\whisper-train\register.py'), '--model', $Model)

    # 6. Stock against tuned on the test calls.
    if (-not $SkipBakeoff) {
        Write-Host 'Comparing with the stock model on the test calls'
        Invoke-Checked 'dotnet' @('run', '--project', 'tools\DcsAtc.VoiceTool', '-c', 'Release', '--', 'bakeoff',
            '--recognizers', 'whisper-turbo-vulkan,whisper-turbo-tuned-vulkan,whisper-turbo-tuned-plain-vulkan', '--out', 'out\train\bakeoff')
        Write-Host "Report: $(Join-Path $Root 'out\train\bakeoff\report.html')"
    }
    Write-Host "Done. In DigiTower: Settings -> Voice requests -> Recognizer: 'Whisper turbo tuned on my voice'."
}
finally {
    Pop-Location
    Stop-Transcript | Out-Null
}
