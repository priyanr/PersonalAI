# Ollama: Docker vs Native Windows vs Native Windows iGPU

Benchmark run on 2026-09-16 to check whether running Ollama natively on Windows,
and/or offloading to the AMD Radeon (Vega 8) integrated GPU, is faster than the
project's default Dockerized Ollama setup.

## Hardware

- CPU: AMD Ryzen 7 5700G (3.80 GHz)
- iGPU: Radeon Graphics (Vega 8, 8 compute units, no dedicated VRAM — shares system RAM)
- OS: Windows 11 Pro

## Method

Same prompt, same model, run three ways:

```
Write a 150 word short story about a lighthouse keeper.
```

Model: `huihui_ai/qwen3.5-abliterated:2b` (already pulled into the project's Docker
Ollama volume, reused for all three runs to keep results comparable).

1. **Docker (current project setup)** — `ollama/ollama:latest` container as defined
   in `docker-compose.yml`, no GPU passthrough configured.
2. **Native Windows, CPU** — Ollama installed via `winget install Ollama.Ollama`
   (v0.34.1), run normally.
3. **Native Windows, iGPU offload** — same native install, restarted with
   `OLLAMA_IGPU_ENABLE=1` (Ollama 0.34.1's Vulkan backend can see the Vega 8 iGPU,
   but drops it by default since it's integrated rather than discrete).

The old unofficial `HSA_OVERRIDE_GFX_VERSION` ROCm trick was tried first and
failed outright (`hipGetDeviceCount failed: 100`, no ROCm-capable device) — Vega 8
(gfx90c) isn't on Ollama's supported ROCm device list on Windows. The Vulkan +
`OLLAMA_IGPU_ENABLE=1` path is the one that actually worked.

## Results

| Setup | Eval rate (tok/s) | Prompt eval rate (tok/s) | GPU used |
|---|---|---|---|
| Docker (current setup) | 11.72 | 80.35 | No (100% CPU) |
| Native Windows, CPU | 10.72 | 98.41 | No (100% CPU) |
| Native Windows, iGPU offload | 11.15 | 17.93 | Yes (100% GPU, 26/26 layers offloaded, confirmed via `ollama ps`) |

## Conclusions

- **Docker overhead is negligible.** Native CPU vs. Dockerized CPU performance is
  within run-to-run noise (~10.7 vs ~11.7 tok/s) — nothing meaningful to gain by
  moving Ollama out of Docker.
- **iGPU offload works but isn't faster.** Generation speed (eval rate) is
  statistically identical to CPU-only. Prompt processing is actually ~5x *slower*
  on the iGPU (17.93 vs 80-98 tok/s CPU).
- **Why:** Vega 8 has only 8 compute units and no dedicated VRAM — it shares the
  same system RAM and memory bandwidth as the CPU, so there's no bandwidth
  advantage to offloading, and its raw compute throughput is too low to win on
  prompt processing (a more compute-bound phase than token generation).
- **Decision:** kept the project on its existing Dockerized Ollama setup — no
  config changes made. Native Ollama's Windows Startup entry was removed so it
  won't compete with the Docker container for port 11434 on login; the native
  install itself is still available on disk if this is worth revisiting on
  different hardware (e.g. a discrete AMD/NVIDIA GPU) in the future.
