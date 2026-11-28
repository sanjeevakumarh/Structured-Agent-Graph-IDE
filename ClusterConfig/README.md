# Local AI cluster

The Mac routes language requests through LiteLLM. Its [cluster configuration](config/macmini.yaml) lists the machines that participate. Each machine's own YAML defines what it runs. Run commands from the `ClusterConfig` directory on the machine named in each step.

**Already deployed?** Start at [switch modes](#4-switch-modes) for daily use or [change membership](#5-change-membership) to test a smaller cluster.

## 1. Choose the nodes and models

Edit `nodes` in **the Mac checkout**. For example, to work with just the Mac and Z820:

```yaml
nodes: [z820, macmini]
```

| To change | Edit |
|---|---|
| Active nodes and gateway routes | [macmini.yaml](config/macmini.yaml) |
| Models and runtime for each mode | The worker's `modes` and `models` sections in [gmini.yaml](config/gmini.yaml), [p520.yaml](config/p520.yaml), or [z820.yaml](config/z820.yaml) |
| Native edge models shared by all four modes | `edge_models` in [nano.yaml](config/nano.yaml) or [ventunoq.yaml](config/ventunoq.yaml) |
| Alternate language checkpoints or video bundles | [abliterated.yaml](config/profiles/abliterated.yaml) or [media.yaml](config/profiles/media.yaml) |
| Inference runtime versions | [versions.yaml](config/versions.yaml) |
| SSH targets and remote checkout path | The controller's private `config/cluster.local.json`. Start from [cluster.example.json](config/cluster.example.json). |

## 2. Deploy the baseline once

1. Put the reviewed checkout on each participating machine at the configured remote path. The controller invokes those copies over SSH. Install working GPU drivers, Tailscale, curl, and either Python 3.12 with PyYAML or uv. The Mac also needs uv and Ollama. Verify SSH access and host keys from the controller.
2. On Windows, create the SSH settings if missing, then edit the copy:
   ```powershell
   if (!(Test-Path config/cluster.local.json)) { Copy-Item config/cluster.example.json config/cluster.local.json }
   ```
3. On each selected node, preview its baseline plan, for example `./setup-z820.sh --dry-run`. **Baseline setup can delete directories listed in `legacy_cleanup`, obsolete managed weights, and unused native model tags.** Review the plan before proceeding.
4. In Windows PowerShell, from `ClusterConfig`, check the connection first:
   ```powershell
   .\cluster.ps1 health -OnlyNode macmini
   ```
   If the Mac is in the active list, reaching `=== macmini: health ===` confirms Windows read the Mac's inventory. An uninstalled gateway can still fail health at this point. If the Mac is outside the list, use a listed node's name instead.

   For a **new baseline**, run `.\cluster.ps1 setup` after reviewing step 3. It uses the checkouts already on the selected nodes and deploys the gateway last if selected. For an **existing baseline**, skip setup. Once the services are ready, run:
   ```powershell
   .\cluster.ps1 health
   .\cluster.ps1 validate
   ```
   Health checks availability and advertised models. Validate submits inference requests.

## 3. Provision the modes you want

**Language workers.** On each selected Linux worker, set `NODE` to its YAML name and provision the standard catalog. This also applies to Z820 if you plan to switch it between language and video service.

```bash
NODE=gmini
bash common/setup.sh --modes provision-catalog --node "$NODE" --catalog standard
```

**Optional abliterated catalog.** On that worker, provision the alternate weights. Pause submissions and wait for active models to unload before validation.

```bash
bash common/setup.sh --modes provision-catalog --node "$NODE" --catalog abliterated
bash common/setup.sh --modes off --node "$NODE" --quiesced
bash common/setup.sh --modes validate-catalog --node "$NODE" --catalog abliterated --quiesced
```

Validation leaves the worker off. Review the generated samples in `~/inference/config/catalogs/abliterated.compatibility.json`. If they pass your quality review, create `abliterated-reviewed.json` alongside it with the report's exact `config_sha256`, `"compatibility_pass": true`, and `"quality_pass": true`. On the Mac, set `catalog_overlays: [abliterated]` and apply routes as in step 5.

**Optional video worker.** On Z820, use an encrypted, non-synced storage directory outside Git. Provisioning needs git, uv, flock, fuser and sudo access. With submissions paused:

```bash
bash common/setup.sh --modes provision-media --node z820 --storage "$HOME/PrivateVideo"
bash common/setup.sh --modes off --node z820 --quiesced
bash common/setup.sh --modes verify-media --node z820
bash common/setup.sh --modes download-media --node z820 --bundle wan22-production
bash common/setup.sh --modes download-media --node z820 --bundle wan22-community  # for video-abliterated
```

The runtime check verifies the accelerator. Clip quality and memory fit need a workflow test on the device. P520 remains a language worker by default; its experimental XPU media backend requires a separate checkpoint and workflow review.

## 4. Switch modes

From the Mac or Windows Git Bash, preview once, then run the command for your workload. Pause submissions and close media tunnels before switching. Mode changes use provisioned files and stop on a busy worker.

```bash
./cluster-mode.sh video --dry-run
./cluster-mode.sh general
./cluster-mode.sh general-abliterated
./cluster-mode.sh video
./cluster-mode.sh video-abliterated
./cluster-mode.sh status
```

| Mode | Gmini | P520 | Z820 |
|---|---|---|---|
| general | GPT-OSS 120B, Qwen3.6 35B with vision, Coder-Next | Qwen3.8 27B Q5 with vision | Qwen3.8 27B IQ4_XS or GPT-OSS 20B |
| general-abliterated | Heretic GPT-OSS and Qwen3.6, abliterated Coder-Next | Heretic Qwen3.8 Q5 | Heretic Qwen3.8 IQ4_XS |
| video | General catalog for prompts and captions | General Q5 | Wan 2.2 A14B text/image-to-video with four-step adapters |
| video-abliterated | Heretic GPT-OSS and Qwen3.6 with vision | Heretic Q5 | Community R1 image-to-video plus upstream A14B text-to-video |

In all four modes the Mac routes language requests. Listed edge nodes serve their existing native models: Nano's Qwen3.5 4B and Ventuno Q's Qwen3 4B. A switch checks their advertised models. Use `--node z820` to switch just Z820. Offline previews use the controller's local copy of the Mac inventory.

For ComfyUI, open a tunnel from your client using the video worker's SSH target:

```bash
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:8188:127.0.0.1:8188 YOUR_VIDEO_SSH_TARGET
```

Open `http://127.0.0.1:8188` and select the provisioned weights in your workflow.

## 5. Change membership

1. On the Mac, remove or add a name in `nodes`. Live health and mode commands read that list on their next run.
2. Wait for current gateway requests to finish, then run `./setup-macmini.sh --apply-routes` on the Mac. Changed routes briefly restart LiteLLM. Worker services and downloads continue.
3. From Windows, run `.\cluster.ps1 health -OnlyNode macmini`. **DEGRADED** means the gateway answered but a listed backend was unavailable or had a stale catalog. For a worker-specific check, use its name with `-OnlyNode`.

A mode switch runs sequentially. If one worker fails, run `./cluster-mode.sh status` before retrying. Worker diagnostics are under `~/inference/config` and `~/inference/benchmarks`. On workers with provisioned modes, the retention marker blocks legacy cleanup and seed operations.

## Use the cluster from OpenCode on Windows

1. Copy [opencode.example.json](config/opencode.example.json) to `opencode.jsonc` in the project you want OpenCode to work on. If the project already has OpenCode settings, merge the `providers.cluster` entry and choose the default `model`. The example lists standard worker models and their configured context sizes.
2. From PowerShell in `ClusterConfig`, open a tunnel to the Mac gateway. Leave this window open:
   ```powershell
   $mac = (Get-Content config/cluster.local.json -Raw | ConvertFrom-Json).targets.macmini
   ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:4401:127.0.0.1:4001 $mac
   ```
3. In a second PowerShell window in `ClusterConfig`, load the gateway credential into this session and check which routes LiteLLM advertises:
   ```powershell
   $mac = (Get-Content config/cluster.local.json -Raw | ConvertFrom-Json).targets.macmini
   $key = ssh -T $mac 'cat ~/inference/config/gateway.key'
   if ($LASTEXITCODE -ne 0 -or $key -notmatch '^sk-[A-Za-z0-9_-]{32,}$') { throw 'Could not read the gateway key.' }
   $env:CLUSTER_GATEWAY_KEY = $key.Trim()
   Remove-Variable key
   (Invoke-RestMethod http://127.0.0.1:4401/v1/models -Headers @{Authorization="Bearer $env:CLUSTER_GATEWAY_KEY"}).data.id
   ```
4. In that same window, change directory to your project and run `opencode`. The default is Gmini's Qwen3-Coder-Next. Use `/models` to choose another available worker. A route can be advertised while its worker is downloading or serving video; select a worker currently serving its language catalog.

For an alternate checkpoint, add its exact gateway alias to the example's model map after provisioning and enabling the alternate catalog. [OpenCode provider reference](https://opencode.ai/docs/providers/)
