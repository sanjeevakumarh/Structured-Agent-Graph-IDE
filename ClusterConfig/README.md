# Local AI Cluster — v0.5

Rebuildable inference on mixed hardware, with a single OpenAI-compatible gateway. Git contains only scripts and configuration.

| Node | Runtime |
|---|---|
| macmini | LiteLLM + Ollama embeddings |
| gmini | llama.cpp Vulkan / ROCm + llama-swap |
| p520 | llama.cpp Vulkan / SYCL + llama-swap |
| z820 | llama.cpp CUDA + llama-swap |
| nano | Ollama on JetPack |
| ventunoq | GenieX on vendor Linux |

## Setup

Use a normal login user with supported OS/GPU drivers. Linux needs `python3`, `python3-yaml`, `curl`, `ca-certificates`, `git`; Mac needs `uv` and running Ollama. Edge setup installs its runtime if missing, using upstream installers. No partitions or GPU driver changes.

Edit `config/<node>.yaml` for models, backend and context; `config/versions.yaml` pins runtime candidates. Default binds are local-only. Set a trusted LAN/tailnet bind and resolvable worker hostnames in `config/macmini.yaml` for remote access. Do not expose unauthenticated edge APIs publicly.

```sh
# On Mac, once. Copy worker.key to ~/inference/config/ on gmini, p520, z820 (chmod 600).
./setup-macmini.sh --init-keys
# On each respective worker: preview, then reconcile (includes legacy migration).
./setup-gmini.sh --dry-run
./setup-gmini.sh
# On each edge board
./setup-nano.sh
./setup-ventunoq.sh
# On Mac, after workers (offline workers may be skipped)
./setup-macmini.sh
```

Use the same setup command on fresh and existing hosts. Mac's client API is `http://macmini:4001/v1`, with the key in `~/inference/config/gateway.key`. Primary workers use port 8081. Gateway routes are derived from worker profiles; rerun Mac setup after model changes. Edge routes are disabled by default; enable explicitly after configuring network access. Model IDs are `node/model-quantization`; no silent cross-server fallback.

## Commands

Pass to the respective `setup-<node>.sh`:

| Argument | Action |
|---|---|
| None / `--install` | Reconcile, test inference, migrate legacy assets and clean up obsolete managed files |
| `--models` | Sync desired models, validate and remove obsolete managed models (workers/edges) |
| `--cleanup` | Remove obsolete managed artifacts |
| `--reset` | Rebuild managed runtime, retain weights; edges reconcile/restart |
| `--migrate` | Explicitly repeat migration; ordinary setup detects it automatically |
| `--health` | Read-only system/API status (not inference) |
| `--validate [--model ID]` | Execute inference on configured models |
| `--benchmark [--model ID]` | Performance test (primary workers require ID; edges optional) |
| `--calibrate [--model ID]` | Measure context on workers/Nano; `--apply` updates YAML |
| `--backend rocm` | Worker backend override |
| `--dry-run` / `--help` | Preview / help |

Mac setup syncs local embedding models and tests local/reachable routes through LiteLLM. Network-unreachable workers are reported as SKIP; HTTP/auth errors fail. Mac `--validate --model ID` requires that specific route to work. Known legacy services are automatically stopped and retired during setup; no manual service changes. Mac model cleanup is not automatic.

## Models and calibration

Edit the YAML catalog, then use `--models`. Worker weights are verified against upstream hashes. Nano uses native Ollama tags; GenieX uses native cached IDs. Edge cleanup only removes previously adopted or explicitly listed `retire_models` tags, after successful validation. Disabled/unlisted external models are retained unless explicitly retired. Nano reports GPU-resident bytes and generation timing; GenieX tests actual completions but does not independently prove NPU placement of an existing service.

Calibration and benchmarking stage missing runtime/weights, pause known GPU services, and restore their prior state afterward (including Ctrl-C). They do not permanently retire services or delete weights. Primary-worker calibration tests real long prompts, retrieval, cache precision, GPU offload, memory and latency; Nano tests context and retrieval with native Ollama timings and before/after memory. `--apply` updates tested YAML settings; rerun setup afterward. Tests can be lengthy and memory checks cannot eliminate OOM risk. GenieX context depends on model bundles; use `--benchmark` rather than claiming a calibrated context. Mac/GenieX context calibration and edge vendor uninstall are not implemented.

## Cleanup and recovery

Review `legacy_cleanup` before setup; optional `migration_hostnames` restricts destructive migration to selected hosts. Setup reclaims explicitly listed oversized legacy or obsolete managed weights on disk pressure after confirmation and retries; `--reclaim-first` allows early legacy removal. Deleted weights cannot be rolled back. Failed migration attempts to restore the previous service. Unknown services and unlisted assets remain untouched. `--yes` bypasses confirmations. Vendor drivers, partitions and unrelated files are preserved. Mac setup disables its known previous LiteLLM launch agent after tests. An already-running GenieX service is reused rather than replaced; its external service configuration/cache are preserved.

`~/inference/` contains `bin/`, `config/`, `models/`, `cache/`, `benchmarks/` and Mac logs. Native edge/Mac Ollama and GenieX stores remain in their existing locations. Preserve keys, weights and caches before reinstalling an OS. No downloaded artifacts or credentials belong in Git.

## Checks

```sh
python3 -m unittest discover -s common
for f in setup-*.sh common/setup.sh; do bash -n "$f"; done
```

Deployment candidate, not hardware-certified. Z820 may require `cuda-native` because it lacks AVX2. Native builds need compatible toolchains. Mac/edge mutable tags and transitive Python packages are not fully locked. Verify real hardware results, network access and model compatibility before destructive cleanup. No synthetic benchmark proves coding quality or concurrency.
