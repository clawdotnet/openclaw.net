# Laya attribution

Laya was developed by **Nandakishor Mukkunnoth (Nandakishor M), ConvAI Innovations**, with contributions from the Laya community. Credit for the model, SDK, and research belongs to its authors and contributors. The OpenClaw.NET adapter and its local serving, input validation, calibration tooling, and compatibility improvements are maintained separately.

- Project and article: <https://laya.convaiinnovations.com/>
- Source: <https://github.com/NandhaKishorM/laya>
- SDK: Laya 0.3.4, Apache-2.0.
- Model weights: <https://huggingface.co/convaiinnovations/laya>, marked Apache-2.0; default pinned revision `1c5edc17a7acd8701df6fc341c0d179f1c62c982`.
- The upstream Apache-2.0 license is retained in `licenses/laya-APACHE-2.0.txt`. Dependency licenses continue to apply independently.
- Research: Nandakishor M, *SalesRLAgent*, [arXiv:2503.23303](https://arxiv.org/abs/2503.23303), 2025; and *Confidence-Aware Routing for Large Language Model Reliability Enhancement*, [arXiv:2510.01237](https://arxiv.org/abs/2510.01237), 2025.

The adapter uses the unmodified PyPI package. Its independent compatibility layer handles Armenian and minority scripts before checkpoint selection, rejects inputs the SDK would truncate, normalizes tokenizer configuration during asset preparation, and identifies pinned models and calibration artifacts in responses. These are integration improvements, not claims of retrained weights, upstream acceptance, author endorsement, or research priority.
