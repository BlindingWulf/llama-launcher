# llama.cpp Launcher: technical details

This page keeps the in-depth behavior notes. For downloading and getting started, see the [README](../README.md).

A Windows Forms launcher for local GGUF models, with a self-contained .NET application and independently replaceable llama.cpp engine. CPU, CUDA 13.3 and Vulkan x64 engines are bundled at b10549 (b2e5e9b28).

## Install and use

Run `Setup.exe`. The installer provides an uninstall entry and Start Menu shortcut. Click Launch on the final installer page, then add a local GGUF, choose a context, and click Start. API clients connect to `http://127.0.0.1:8080/v1` by default. The Network page copies the URL, API key and model ID. A desktop shortcut can be created from Settings.

Requirements: Windows 10 22H2 or Windows 11 x64, sufficient memory for your model, and a vendor GPU driver for GPU acceleration. Windows includes .NET Framework 4.8 for the setup host. The application itself includes .NET 8.0.28. The installer embeds the signed Microsoft Visual C++ x64 runtime and requests elevation only if that machine prerequisite is missing or older than 14.44.35211. On Start with LAN enabled, a missing or outdated LocalSubnet firewall rule requests elevation. The Network page also provides an explicit repair button. Application files and profiles are per user.

Installation does not bundle or move GGUF models. Model files can remain anywhere you can read them. For split GGUFs, select `-00001-of-NNNNN.gguf`; every shard must be beside it. A matching vision projector can be selected under Advanced. Unknown model capabilities are not represented as verified support.

## Existing Qwen installation

A fresh installation starts with no registered models, LAN disabled, automatic update checks disabled, and a newly generated API key. It does not automatically import legacy Qwen settings. Importing an existing Qwen configuration is an explicit action from Settings; command-line users can opt in with `--import-legacy`. Only an explicit import can retain that configuration's API key, port, LAN setting, model paths, and presets. An upgrade preserves this device's existing launcher settings.

The imported current profile produces the original server arguments. Once a server is ready, Start verifies its model ID and effective context through the local API before calling the existing `Sync-QwenCodeSettings` function. The explicit Sync button requires the selected live server and no pending restart. The Network page copies verified live connection details. Synchronization is only available when the original compatibility script and Qwen settings path are configured; installation and import alone do not invoke it. No Qwen reinstallation is required.

The installer does not install local routing scripts, change Qwen Code files, configure a firewall rule, or start a model. Those integration and network actions require configuration in the app. Do not run two servers on the same port: the launcher refuses occupied ports and never stops a server it did not start.

## Configuration and process ownership

Settings are in `%LOCALAPPDATA%\LlamaLauncher\settings.json`, with atomic replacement and a previous valid backup. Launcher/theme/network/model-directory preferences are global; inference options and memory-safety policy are stored per model. API keys are private local settings, not included in diagnostics or source archives. An invalid configuration recovers from backup. A failed startup offers a conservative CPU launch without overwriting the profile.

Memory safety is launcher admission policy only; it never emits a llama.cpp argument or changes context, cache precision, or offload automatically. New profiles default to Balanced. Profiles saved before 1.1.0 migrate to Off because earlier versions displayed estimates without enforcing them, preserving their launch behavior. Relaxed and Balanced ask before a likely over-budget launch, Strict blocks it, and Off attempts the exact selected configuration while keeping the fit estimate informational. Deliberately choosing a non-Balanced policy shows a small temporary notice beside the setting instead of a modal dialog. Custom stores explicit VRAM and available-system-RAM reserves plus Warn, Confirm, or Block behavior. Runtime allocation can still fail, spill where supported, or become very slow.

The process manager launches without a terminal, captures bounded stdout/stderr, verifies the model ID and context through the local API, and places the server in a kill-on-close Windows job. A running server is labeled **Live** in the launcher, which explains that closing the window keeps the model loaded. On reopen, the launcher verifies the port owner's process identity and reconnects to that same server with a **Live · reconnected** status and Stop and Restart available; it never starts a duplicate or reloads the model. The first reopen after an upgrade can recover the previous release's server by verifying its bundled executable and exact model, alias, and port arguments. An abrupt launcher termination still ends a server in its launch job. Stop ends the connected server after attempting normal close. A server that fails identity checks remains external and cannot be stopped by the launcher. Finish active requests before pressing Stop.

MTP is supported when GGUF nextn/MTP metadata and tensors identify it, or through an imported tested preset. Missing evidence stays Unknown; explicit zero layers is Unavailable. A separately labeled experimental override exists. Auto never guesses from filenames. N-gram can operate separately or in a combined speculative pipeline with MTP on a compatible runtime. Every selected mode is checked against engine help.

Learned PLE n-gram tables are part of the model weights and are separate from the Advanced speculative N-gram control. Runtime checks now require exact flags and advertised mode names, and reject removed options. Learned-table counts include every GGUF shard; memory and table placement remain approximate until the model runs. Future learned-table variants retain their settings during Auto Optimize instead of inheriting the measured Flash-Next preset or its automatically selected MTP head. See [Qwen 4 readiness and release-day validation](qwen4-readiness-2026-10-04.md) for the supported preparation work and remaining model/runtime dependencies.

Memory estimates reserve compute space and OS headroom, use model tensor dimensions for KV cache, and adjust for GPU offload. Hybrid/recurrent model estimates and non-NVIDIA available VRAM are approximate. They are planning guidance, not a guarantee. Auto Optimize chooses a conservative context; existing optimized presets remain available separately. For multi-GPU systems this release uses a single GPU; backend override is global.

## Ollama API

For apps that only accept Ollama, the footer **Ollama API** switch (also on the Network page) runs `ollama/ollama_shim.py`, a standard-library Python server on Ollama's port, `http://127.0.0.1:11434`. It translates Ollama's API (chat, generate, tags, show, tools, images, thinking, JSON formats, and Ollama's `/v1`) to the loaded server's OpenAI API, whether that's llama.cpp or PolyStrata. The server's own port, API and key are unchanged. It reads the port and key from `settings.json`, so model, port and key changes need no restart. Any requested model name goes to the loaded model, so a request never switches models. It needs Python 3.9 or later (`pythonw.exe` on PATH, or PolyStrata's venv).

While it's on, it keeps running when the window is closed and starts at sign-in through an HKCU Run entry. Turning it off stops it and removes that entry. **Share it on my Tailscale network** adds `tailscale serve` TCP forwarding of port 11434 on the tailnet addresses only. tailscaled does the forwarding, so no firewall rule is involved, and turning the API off removes it. Like Ollama, it requires no key unless **Require an API key** is on. Then clients send `Authorization: Bearer <key>`, `X-API-Key`, or Basic auth with the key as the password. `GET /` stays open for detection, and key changes apply on the next request. `/v1` passes through only Ollama's own endpoints (chat/completions, completions, embeddings, models), so the server's load, unload, VRAM and status controls stay behind its key. The log is `logs/ollama-shim.log` in the data folder. It records only start, stop and errors, and never message text. **Debug log** (off by default) adds one line of request details per request: endpoint, model, counts and timing.

PolyStrata's vision encoder keeps the encoded pictures of requests in `strata-vision-*` folders (in the temp folder) and never removes them. The launcher removes the folders of ended servers after Stop and when it opens. It keeps the folder of a live server, and never touches a folder that a process is still using.

## Basalt for Flash-Next

Basalt is an additional local engine for Qwen3.8 Flash-Next on NVIDIA Blackwell GPUs (the GeForce RTX 50-series, such as the RTX 5070, 5080 and 5090); it does not run on older GPUs. Install a Windows release from [Basalt](https://github.com/jesdga95/basalt), including its Python environment and CUDA 13 libraries, then choose its folder on the Engines page. On the Model page, select the original GGUF profile, choose Basalt as its engine, and pick a converted `.basalt` model pack; the pack is saved per model. The launcher keeps the GGUF profile as the model identity and serves the pack through the same authenticated local API and model alias. PolyStrata remains separately selectable.

The launcher requires a version 2 pack with the image projector and MTP sections. Basalt's [conversion guide](https://github.com/jesdga95/basalt/blob/main/docs/model-file.md) describes converting a GGUF with `--mmproj` and `--mtp-source`. Some quantizations require `--compat-bf16`. Converting a model makes another large file; keep enough free disk space. A converted pack can perform differently from an official tuned pack. The launcher starts dynamic vision and an eight-token MTP verification window, and passes the chosen context, KV cache precision and PolyStrata / Basalt slot count. Basalt manages its own expert placement and memory; llama.cpp-specific controls do not tune Basalt. It listens on loopback; the launcher's Tailscale forwarding can share that local API. Basalt's Windows release and quality should be validated on the target machine.

## Engine updates

Check for updates (header or Engines page) checks the official llama.cpp GitHub releases for the newest complete Windows x64 `b` build, including the matching CPU, CUDA 13, CUDA runtime, and Vulkan assets. The result is informational: the user must separately choose Download and install. The app verifies every downloaded asset against GitHub's SHA-256 digest, builds a private bundle, verifies its manifest, and probes CPU, CUDA, and Vulkan before switching the bundled engine. Model files and saved profiles are not changed. The previous engine remains available for rollback; start a model after staging an update to confirm inference. The Qwen3.8-Flash-Next TurboQuant runtime is a separate experimental build and is not overwritten by the upstream update.

Automatic checking when opening the Engines page is optional and off by default. Pressing Check for updates in the header always checks immediately. Engine updates are rolling upstream builds and may include prereleases; the exact build and download size are shown before installation. A server must be stopped before installing or rolling back an engine.

An offline engine bundle is a folder containing `manifest.json` (mapping relative file paths to SHA-256 digests), and `cpu/`, `cuda/`, `vulkan/` runtime folders. The bundled runtime directory is an example. Use bundles from a trusted source: the manifest verifies integrity, not publisher identity. Install Engine Bundle copies verified files to a unique private directory and probes the selected runtime plus CPU fallback before atomically changing the active pointer. The previous runtime is retained. Start a model to confirm inference, and use Roll Back Engine if it fails. A failed staging operation never changes the active engine. Staged directories are retained for investigation; disk usage can grow across updates.

## Reproduce the build

Build prerequisites: Windows x64 and PowerShell 7 for the build script, internet for dependency downloads, and roughly 5 GB free build space. End users do not need PowerShell, Python, a compiler, or a manual llama.cpp installation.

```powershell
pwsh -File build.ps1
```

The script downloads a local .NET SDK 8.0.425, WiX 5.0.2 build tools, and pinned official engine/VC runtime assets. Downloaded engine and SDK/runtime dependencies are checked against `dependencies.lock.json`. NuGet dependencies use `src/packages.lock.json`. It publishes the self-contained application, runs isolated self-tests, creates the MSI and WiX installer bundle, and embeds the bundle in a small SHA-256-verifying Setup.exe host. The host extracts only to a new restricted temporary directory and launches the securely named WiX bundle; it does not rename Burn to Setup.exe, which WiX rejects because of Windows compatibility shims.

Outputs: `dist/Setup.exe`, `dist/source.zip`, `dist/build-report.txt`. The report records actual sizes and SHA-256 values. The build is source-reproducible; generated MSI identifiers/timestamps and executable resources mean rebuilding is not guaranteed to produce identical bytes. No application code-signing identity or certificate is included.

To run optional regression tests against the existing Qwen installation:

```powershell
pwsh -File tests/baseline.ps1
& .\publish\LlamaLauncher.exe --self-test "$PWD\.private" --legacy --inference --render | Out-Null
```

The baseline test reads the private configuration, launches the existing server directly, tests the API, and stops only that test process. It does not run Qwen synchronization. Do not run this while another server uses the configured port. `--generic` additionally runs a local `smollm2-test.gguf` from the test output directory. Models and private regression outputs are excluded from the source archive. UI renders simulate scale factors; they do not change global Windows display settings.

To check responsiveness, run `LlamaLauncher.exe --perf-test <output-folder> [model.gguf]`. It uses an isolated settings folder and writes `performance.txt` with page-switch times and resize frame times (a full reflow on every step, and a live drag where the page reflows at most every 80 ms). The pages use a lightweight stack/row layout (`Box` in `UiKit.cs`) that measures each control once per width and skips hidden pages; nested auto-sized TableLayoutPanels and a native tab control made page switches take about 0.4 s and resizing run near 5 fps.

## Source components

- `Configuration.cs`: profile storage, migration, backups.
- `GgufInspector.cs`: bounded streaming GGUF header/tensor inspection.
- `Hardware.cs`: WMI CPU, DXGI GPU, NVIDIA memory query, RAM, memory estimator.
- `Arguments.cs`: centralized argument contract and validated custom sampling extension.
- `RuntimeManager.cs`: health checks, process/job ownership, logs and redacted diagnostics.
- `EngineManager.cs`: backend probes, staged engine updates and rollback.
- `Integration.cs`: LocalSubnet firewall, optional existing Qwen adapter, shortcuts.
- `MainForm.cs`: responsive Windows Forms interface and themes.
- `SelfTests.cs`: service, migration, inference and rendering acceptance checks.
- `installer/`: WiX MSI/bundle definitions and the Setup host.

Full licensing details are installed under `licenses/`. See `docs/ACCEPTANCE.md` for results and limitations.

The Network page also has **Share / repair model API** for the main server port, independent of the Ollama interface. It forwards the authenticated localhost API through Tailscale Serve for either llama.cpp or PolyStrata, persists while the launcher is closed, and repairs the route on opening the launcher and after starting a model. The server API key is required. Use `http://<Tailscale IP>:<server port>/v1`. Changing ports moves the managed route on the next model start; **Stop sharing model API** removes the last managed route. Existing unrelated Tailscale services are preserved.
