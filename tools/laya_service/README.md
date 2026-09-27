# Local Laya Service

This standalone .NET 10 CLI downloads pinned model assets, serves NLaya decisions over loopback HTTP, evaluates labeled cases, fits v2 calibration artifacts, and reports Jev/Laya routing journals. It uses `NLaya` and `NLaya.TorchSharp` 1.0.0 with `TorchSharp-cpu` 0.107.0. The Gateway remains a separate process and does not reference the inference packages.

Start with the [operator guide](../../docs/laya-routing.md) for `download`, `serve`, `evaluate`, `calibrate`, and `report` commands, artifact handling, Gateway configuration, verification, and rollback. The service is currently documented for JIT deployment; NativeAOT support is not claimed. CPU is the verified backend with the checked-in package set.

Laya is developed by Nandakishor Mukkunnoth, ConvAI Innovations, and upstream contributors. Read [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for author credit, research links, and the retained upstream license. The SDK and weights remain upstream artifacts.
