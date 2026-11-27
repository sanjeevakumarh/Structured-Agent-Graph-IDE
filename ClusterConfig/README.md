# Local AI Cluster — v0.9

Reproducible local inference on mixed hardware. Git stores scripts and YAML only; weights, binaries and credentials stay local.

| Node | Runtime |
|---|---|
| macmini | LiteLLM + Ollama embeddings |
| gmini | llama.cpp Vulkan / ROCm + llama-swap |
| p520 | llama.cpp Vulkan / SYCL + llama-swap |
| z820 | llama.cpp CUDA + llama-swap |
| nano | Ollama on JetPack |
| ventunoq | GenieX on vendor Linux |

## Setup

Use a normal login user. Linux needs Python 3 with PyYAML, curl, git and working GPU drivers; Mac needs uv and Ollama. Edge setup installs missing native runtimes. No partition or driver changes. Review `config/<node>.yaml` and `config/versions.yaml` first.

```sh
# On Mac once; copy worker.key to ~/inference/config/ on the three workers (chmod 600).
./setup-macmini.sh --init-keys
# On each worker or edge, using its own node name:
./setup-gmini.sh --version    # expect v0.9
./setup-gmini.sh --dry-run
./setup-gmini.sh
# Once workers are ready, on Mac:
./setup-macmini.sh
```

Replace old source files, not `~/inference/`. Verified weights and resumable `.partial` downloads are reused. Default binds are local-only. For remote access, set `bind: tailnet` on Mac and primary workers and configure resolvable worker hostnames in `config/macmini.yaml`. The client API is `http://macmini:4001/v1` with `gateway.key`. Workers listen on port 8081. Edge routes are optional and disabled by default. Explicit `node/model-quantization` IDs do not fail over to other machines.

## Commands

Use the appropriate `setup-<node>.sh`:

| Option | Action |
|---|---|
| None / `--install` | Install, reconcile, validate, migrate old services and prune obsolete managed weights |
| `--models` | Reconcile the YAML model catalog |
| `--cleanup` | Remove obsolete managed assets |
| `--reset` | Rebuild managed runtime, preserve weights |
| `--health` | Read-only service and model discovery |
| `--validate [--model ID]` | Actual inference test |
| `--benchmark [--model ID]` | Throughput test (worker requires ID) |
| `--calibrate [--model ID]` | Context tests; `--apply` writes recommendations to YAML |
| `--backend rocm` | Override worker backend |
| `--migrate` | Explicit legacy migration (usually automatic) |
| `--help` | All options |

## Models and validation

Edit the node YAML, then run setup. Disabled model entries retain weights; removed entries become cleanup candidates. Worker weights use pinned upstream GGUF revisions and verified hashes; raw Ollama blobs are not trusted as sources. Exact-hash matches can be reused. Edge and Mac native model tags are mutable.

Worker calibration pauses/restores known services, tests concurrent configured slots and stops at the target context. Intel/AMD use vendor counters or fresh backend-reported memory budgets if counters are missing; unknown telemetry fails closed. Nano samples unified RAM, adjusts prompt size using measured tokens, and reports failed checks. `--apply` changes only measured settings; rerun setup to deploy. Full calibration can be lengthy. GenieX QAIRT bundle context is fixed: `--calibrate` runs a real benchmark instead of recommending an unverified context. VENTUNO Q's failing Gemma plugin is disabled by default; cached weights remain. Qwen is enabled. NPU placement is not independently verified.

## Cleanup and recovery

Review `legacy_cleanup`. Setup stages verified files, stops known legacy services, tests inference, fresh GPU-offload evidence and deployed-context performance, then retires explicitly listed assets. Failed validation attempts rollback; unknown services/files are preserved. On disk pressure it offers listed early reclamation and retries. Early deletion cannot be rolled back. `--reclaim-first` explicitly requests early deletion; `--yes` bypasses prompts. No OS partition or driver changes. Mac skips unreachable workers but fails on auth/protocol or reachable-model errors.

Managed files live under `~/inference/{bin,config,models,cache,benchmarks}`. Native vendor stores remain in place. Preserve keys/models/cache before OS reinstall. Failure reports are in `benchmarks/`. Worker native startup logs are captured per model in private `config/` and cleared at smoke-test restart. Verbosity 4 may include prompt data and grow logs; automatic native/Mac log rotation is not implemented. Review logs before sharing.

## Tests and limits

```sh
python3 -m unittest discover -s common
for f in setup-*.sh common/setup.sh; do bash -n "$f"; done
```

Deployment candidate; real hardware validation still required. GPU budget readings are driver estimates, not guaranteed physical free memory. Edge memory sampling is observational. Resident groups require separate combined-memory tests. GenieX plugin repair and clean-OS GPU driver installation are outside this package. Mac model cleanup and vendor uninstall remain manual. Remove legacy migration code after cutover.
