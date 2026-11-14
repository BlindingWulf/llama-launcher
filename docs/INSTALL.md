# Installing llama.cpp Launcher

Run `LlamaLauncher-Setup.exe` (named `Setup.exe` in local builds). The setup file includes the application, its .NET runtime, CPU/CUDA/Vulkan engines, and the Microsoft Visual C++ runtime prerequisite. No model files are included. Windows 10 22H2 or Windows 11 x64 is required. GPU acceleration also requires a suitable vendor driver; CPU mode is available without a supported GPU.

Setup creates a per-user installation and Start Menu shortcut. A machine-wide Visual C++ prerequisite may request administrator approval. The application itself runs without administrator rights. Installation does not enable LAN, create firewall rules, configure a remote server, or change another application.

On a fresh launch, the app asks which engine to use (llama.cpp with CUDA, Vulkan or CPU, Basalt for NVIDIA RTX 50-series cards, PolyStrata, or a custom llama.cpp build) and where your models are. The model list starts empty, LAN and automatic update checks are off, and the app generates a new local API key. Add a GGUF on the Model page. For split models, keep all shards together and select the first shard. Start with a small context, start the model, then use the connection details displayed by this device's launcher.

Settings belong to the current Windows user under `%LOCALAPPDATA%\LlamaLauncher`. Existing settings on that device are preserved during upgrades. The installer contains no saved settings, API keys, network addresses, model profiles, logs, screenshots, or engine update state from the build computer.

Legacy Qwen configuration import is optional and explicit. Use Settings > Import in the app, or `--import-legacy` when intentionally migrating the local legacy configuration. An explicit import retains that selected configuration's network settings and key; ordinary first launch does not discover or import it automatically.

For GPU models, the driver, available memory, runtime architecture support, and model quantization determine compatibility. The experimental TurboQuant/Flash-Next engine is built for NVIDIA sm_120a hardware. Other devices should use the standard bundled engine and supported models. Model downloading is separate from installation.

Uninstall through Windows Installed apps. Personal settings and models are retained. The installer is unsigned; a publisher certificate is required to establish a signed publisher identity.
