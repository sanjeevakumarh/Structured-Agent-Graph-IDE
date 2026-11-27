# Local AI Cluster v0.11 — seed deployment

Git contains the desired inference stack, not models, binaries or credentials. No legacy rollback. All future changes are driven by `config/*.yaml`.

## Setup

**Destructive:** ordinary setup deletes the inventoried old inference directories and services in `legacy_cleanup`, including the entire legacy worker `~/models` directories where listed, old Ollama stores and unused native model tags. Review the YAML before running. No OS, partition, driver or unrelated application changes. Existing keys, reports, pinned download caches and exact upstream-matching desired weights are retained.

Requirements: working GPU drivers, Tailscale and resolvable cluster hostnames, Python + PyYAML (or uv), curl; Mac also needs uv and Ollama. Vendor JetPack/QAIRT stacks remain installed. Run as normal login user, not root.

1. Replace the old repository files with this complete package on every node. Do not merge old YAML or generated configurations.
2. From Windows, edit SSH mappings in `cluster.ps1` if needed, then run `./cluster.ps1 setup`. It checks versions, initializes/distributes keys, runs workers and edge nodes, then Mac. Sudo may prompt. `-Yes` approves destructive actions.
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
