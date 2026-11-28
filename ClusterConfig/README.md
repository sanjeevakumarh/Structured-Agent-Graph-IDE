# Local AI Cluster v0.11 â€” seed deployment

Git contains the desired inference stack, not models, binaries or credentials. No legacy rollback. All future changes are driven by `config/*.yaml`.

## Cluster modes

Run from the Mac or Windows Git Bash with Python + PyYAML and the SSH mappings in `config/cluster.local.json`. Pause submissions and close media tunnels before switching. Use `--dry-run` to see the exact models without connecting, or `--node z820` to target one worker.

```bash
./cluster-mode.sh general
./cluster-mode.sh general-abliterated
./cluster-mode.sh video
./cluster-mode.sh video-abliterated
./cluster-mode.sh status
```

| Mode | Gmini | P520 | Z820 |
|---|---|---|---|
| general | GPT-OSS 120B, Qwen3.6 35B with vision, Coder-Next | Qwen3.8 27B Q5 with vision | Qwen3.8 27B IQ4_XS or GPT-OSS 20B |
| general-abliterated | Heretic GPT-OSS 120B and Qwen3.6 35B with vision, abliterated Coder-Next | Qwen3.8 27B Heretic ARA Q5 | Qwen3.8 27B Heretic ARA IQ4_XS |
| video | General catalog for prompts and captions | General Q5 | Wan 2.2 A14B FP8 text/image-to-video with matching LightX2V adapters |
| video-abliterated | Heretic GPT-OSS 120B and Qwen3.6 35B with vision | Heretic Q5 | Community R1 image-to-video pair plus upstream A14B text-to-video |

Edit the `modes:` section in [gmini.yaml](config/gmini.yaml), [p520.yaml](config/p520.yaml), or [z820.yaml](config/z820.yaml) to select models. Shared YAML anchors reuse identical selections. Checkpoint pins live in the node's `models:` section and the [optional catalogs](config/profiles/). Mac serves LiteLLM throughout. P520 defaults to language service until its experimental XPU backend passes a local workflow test.

Provision each selected catalog once with `python3 common/modes.py provision-catalog --node NODE --catalog standard` or `--catalog abliterated`. Stage the alternate aliases on the stopped Mac gateway with `provision-router --node macmini`, then `router-start --node macmini`. The alternate catalog requires `validate-catalog` and a quality review. On Z820 provision ComfyUI with `provision-media --node z820 --storage "$HOME/PrivateVideo"`, switch the idle node `off`, then run `verify-media` and `download-media --bundle wan22-production` with the same node argument. Provision `wan22-community` separately for video-abliterated. Select the corresponding checkpoint(s) in the native ComfyUI workflow. Switching retains installed models and stops on a busy worker. A cluster switch applies sequentially; status shows which workers completed.

Use encrypted local storage and access ComfyUI through `ssh -N -L 127.0.0.1:8188:127.0.0.1:8188 YOUR_Z820_SSH_ALIAS`. SDXL portraits and Wan 14B image-to-video are optional bundles in the media manifest.

The checkpoint inventory records upstream revisions and exact files. The P520 Heretic Q5 publisher supplies no vision projector. Use Gmini for captions in the alternate modes. The Z820 5B preview remains available through the `wan22-5b` bundle. A14B throughput and peak memory on the A4500 are unmeasured.

Before increasing concurrency, measure the Gmini 120B + 35B pair at the intended context and slot counts, including KV caches and vision buffers. `reserve_gib` reserves disk space. Compare P520 Vulkan and SYCL with the same GGUF once oneAPI is available. For video, compare four-step A14B at 832×480/81 frames with the 5B preview, recording wall time, RAM, VRAM and output quality.

## Setup

**Destructive:** ordinary setup deletes the inventoried old inference directories and services in `legacy_cleanup`, including the entire legacy worker `~/models` directories where listed, old Ollama stores and unused native model tags. Review the YAML before running. No OS, partition, driver or unrelated application changes. Existing keys, reports, pinned download caches and exact upstream-matching desired weights are retained.

Requirements: working GPU drivers, Tailscale and resolvable cluster hostnames, Python + PyYAML (or uv), curl; Mac also needs uv and Ollama. Vendor JetPack/QAIRT stacks remain installed. Run as normal login user, not root.

1. Replace the old repository files with this complete package on every node. Do not merge old YAML or generated configurations.
2. From Windows, copy `config/cluster.example.json` to `config/cluster.local.json`, set the SSH targets and remote checkout path, then run `./cluster.ps1 setup`. The local file is Git-ignored; keep it only on the controlling machine. It checks versions, initializes/distributes keys, runs workers and edge nodes, then Mac. Sudo may prompt. `-Yes` approves destructive actions.
3. Alternatively run `./setup-macmini.sh --init-keys`, copy `~/inference/config/worker.key` to the three workers with mode 600, then execute each node's setup script; Mac last.
4. Run `./cluster.ps1 health` and `./cluster.ps1 validate`. Gateway validation requires enabled workers to be reachable; edge routes are disabled by default.

## Daily use

| Command on a node | Action |
|---|---|
| `./setup-gmini.sh --dry-run` | Preview |
| `./setup-gmini.sh` | Seed/reconcile, delete obsolete assets, install and validate |
| `./setup-gmini.sh --seed` | Force clean runtime re-extraction/rebuild |
| `./setup-gmini.sh --models` | Apply model catalog changes |
| `./setup-gmini.sh --health` | Check service and catalog |
| `./setup-gmini.sh --validate` | Actual generation and tool-call tests |
| `./setup-gmini.sh --benchmark` | Measure all enabled models |
| `./setup-gmini.sh --calibrate` | Measure context/cache options |
| `./setup-gmini.sh --calibrate --apply` | Save passing recommendations to YAML; rerun setup |
| `./setup-gmini.sh --help` | Other commands |

Workers use authenticated Tailscale HTTP on port 8081; gateway uses port 4001. No IP addresses in Git. Model IDs identify the actual server and quantization. Edit `config/<node>.yaml` to replace models, and `config/versions.yaml` to change runtimes. Disabled models are not retained in the seed catalog.

## Implementation and limits

Worker seed resolves pinned upstream GGUF manifests before deletion, reuses only exact SHA-256 matches, removes old weights before downloads, regenerates configuration and tests actual inference, structured tool calls and GPU offload. No prior config is restored on failure. Failures include native logs under `~/inference/benchmarks` and `~/inference/config`. Calibration is separate from installation. Keep the local artifact cache for fast rebuilds; a full OS wipe requires downloads or backup. Do not copy secrets into Git.

Mac and edge native runtimes are reused; Ollama tags and GenieX bundles are vendor-managed, not content-pinned like worker GGUFs. VENTUNO Q calibration benchmarks its fixed-context bundle instead of guessing a context. Nano uses Qwen only; failing Gemma models are removed. Hardware compatibility, NPU placement and optimum contexts require actual device tests; no hardware certification is claimed.

`sudo bash setup-os-maintenance.sh --install-timer` schedules daily Linux maintenance. Review vendor-specific updates on edge devices first.

Offline regression tests: `python3 -m unittest discover -s common`. Do not run deployment scripts on a test host with unrelated inference data.
