# Local evaluation demo

From the repository root:

```sh
python3 samples/OpenClaw.LocalDemo/run.py
```

Requires Python 3.11+, .NET 10 and Docker. No provider account or API key is needed.
The first run downloads an Ollama image and the `llama3.2:3b` model (several GB).
CPU inference works but can be slow. Use `--model <ollama-tag>` to select another model.

The runner builds the gateway, starts its own loopback-only Ollama container, pulls
and caches the model, creates a synthetic project note, and opens browser chat.
It prints a suggested task that reads the note and creates a plan. Only `read_file`
is exposed; private prompt files, memory recall, plugins and shell are excluded.

Ctrl+C stops only the processes/container created by this run. It retains the
printed temporary directory (config, synthetic workspace, memory and log) and
`openclaw-demo-models` Docker volume. Existing gateway settings are not changed.
Each run uses fresh ports and state. `--no-browser` prints the URL without opening
it; `--prepare-only` checks config generation without downloading or starting anything.
