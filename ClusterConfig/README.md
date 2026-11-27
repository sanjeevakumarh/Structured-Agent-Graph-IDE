# Local AI Cluster — v0.3

Rebuildable local inference with hardware-specific backends and one OpenAI-compatible gateway. Git stores scripts and configuration, not weights, binaries or secrets.

| Node | Runtime | Role |
|---|---|---|
| `macmini` | LiteLLM, Ollama | Gateway, embeddings |
| `gmini` | llama.cpp Vulkan / ROCm | Large models |
| `p520` | llama.cpp Vulkan / SYCL | Medium models |
| `z820` | llama.cpp CUDA | CUDA worker |
| `nano` | JetPack, Ollama | Edge (optional route) |
| `ventunoq` | GenieX | Edge (optional route) |

## Setup

Use a normal login user with compatible GPU drivers. Install missing prerequisites:

```sh
# Linux
sudo apt-get install python3 python3-yaml curl ca-certificates git
# Mac (Homebrew; Ollama installed separately)
brew install uv
```

1. Clone/copy this repository onto each machine. Edit `config/<node>.yaml` for desired models, context, backend and trusted network bind. Default binds are local-only. Edit hostnames in `config/macmini.yaml`; use DNS or MagicDNS names that resolve from the gateway. Never expose unauthenticated edge services publicly.
2. On Mac, generate keys; copy the worker key to the three primary workers (adjust SSH aliases):

```sh
bash setup-macmini.sh --init-keys
for host in gmini p520 z820; do
  ssh "$host" 'umask 077; mkdir -p ~/inference/config; chmod 700 ~/inference/config'
  scp ~/inference/config/worker.key "$host:~/inference/config/worker.key"
  ssh "$host" 'chmod 600 ~/inference/config/worker.key'
done
```

3. Deploy workers first. On a clean OS use ordinary setup; for existing infrastructure use migration below.

```sh
bash setup-gmini.sh --dry-run
bash setup-gmini.sh
# repeat for p520 and z820
```

4. On Mac, using the same profile files as the workers:

```sh
bash setup-macmini.sh --models
bash setup-macmini.sh
```

Clients use `http://macmini:4001/v1` and `~/inference/config/gateway.key`. Workers use port 8081. Models are named `node/model-quantization`, e.g. `gmini/qwen3-coder-next-80b-q4_k_m`. Gateway routes derive from enabled worker models; reapply the gateway after catalog changes. Offline workers do not block startup. Edge routes are disabled by default; enable only after verifying their OpenAI API and model IDs. Optional short aliases target one explicit route, without cross-node failover. Gateway `--health` checks all six nodes, including disabled edges.

## Manage

Edit the node YAML for models and options; pin upstream revisions, hashes and filenames. Update `config/versions.yaml` for runtime releases. Commands below are arguments to the node's setup script.

| Command | Action |
|---|---|
| `--models --prune` | Sync, validate, calibrate deployed settings, remove obsolete managed weights |
| `--cleanup` | Remove obsolete owned artifacts |
| `--reset` / `--purge` | Rebuild runtime / remove managed weights and runtime |
| `--health` / `--validate --model ID` | Status / inference test |
| `--benchmark --model ID` | Basic prompt and generation benchmark |
| `--calibrate [--model ID]` | Context/cache recommendation |
| `--install --backend rocm` | Install/reconcile with backend override |
| `--dry-run` / `--help` | Preview / help |

## Context calibration

```sh
bash setup-gmini.sh --calibrate --model gmini/qwen3-coder-next-80b-q4_k_m
bash setup-gmini.sh --calibrate --model gmini/qwen3-coder-next-80b-q4_k_m --apply
```

Default contexts are initial settings, not measured limits. Calibration temporarily stops the worker, installs a requested backend if missing, and tests configured context sizes up to native model capacity with real ~90%-full prompts, retrieval at three depths, streaming TTFT, throughput, GPU offload, KV precision, memory headroom and swap. Q8 is tried first, F16 compared, Q4 tried if the goal is not met. Largest passing context wins; higher cache precision breaks ties. Tune thresholds and target in YAML. Failed candidates stop larger tests for that cache. Missing telemetry prevents a pass. Tests may take hours; sampling cannot guarantee protection from OOM. `--backend` compares another backend; `--cache-type` restricts the cache test. `--apply` updates only tested scalar fields in YAML, not the live service; run setup again. Reports/logs live in `~/inference/benchmarks/`. Mac/edge calibration is not implemented.

## Clean migration

```sh
bash setup-gmini.sh --migrate --dry-run
bash setup-gmini.sh --migrate
```

**Review `legacy_cleanup` and `migration_hostnames` first.** These are reference-fleet examples. Migration verifies and reuses weights, stops the old service, tests all enabled models including long prompts at deployed context, then retires listed legacy assets. Failure attempts to restore the old service. `--reclaim-first` explicitly allows listed early deletions if disk space is tight; those cannot be rolled back. `--yes` bypasses confirmation. Mac migration retires its old gateway after local/reachable route tests. Edge vendor services, drivers, partitions and unrelated files are untouched. Prune failures retain old weights; previous native config is retained for manual recovery.

## Layout and checks

```text
~/inference/
  bin/          runtimes
  config/       generated config and keys
  models/       verified weights
  cache/        reusable downloads
  benchmarks/   results
  logs/         Mac logs (Linux: journal)
```

```sh
python3 -m unittest discover -s common
for f in setup-*.sh common/setup.sh; do bash -n "$f"; done
```

Deployment candidate, not hardware-certified. OS/driver installation remains manual; Z820 may need `cuda-native` (no AVX2). Native builds need compatible toolchains. Mac/edge Ollama tags and Python transitive dependencies are not fully locked. Verify Intel telemetry, slash IDs in clients, model templates/tool calls and actual GPU performance on each host before cleanup. Calibration is synthetic single-slot text testing, not a complete quality, concurrency or prefix-cache benchmark. Preserve weights, keys and cache separately before wiping an OS.

Upstream: [llama.cpp](https://github.com/ggml-org/llama.cpp) · [llama-swap](https://github.com/mostlygeek/llama-swap) · [LiteLLM](https://docs.litellm.ai/) · [Ollama](https://ollama.com/)
