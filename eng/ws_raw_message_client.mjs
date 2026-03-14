#!/usr/bin/env node

import fs from "node:fs/promises";

function parseArgs(argv) {
  const args = new Map();
  for (let i = 2; i < argv.length; i += 2) {
    const key = argv[i];
    const value = argv[i + 1];
    if (!key?.startsWith("--") || value === undefined) {
      throw new Error("Expected --key value pairs.");
    }
    args.set(key.slice(2), value);
  }
  return args;
}

async function main() {
  const args = parseArgs(process.argv);
  const url = args.get("url");
  const message = args.get("message");
  const output = args.get("output");
  const timeoutMs = Number.parseInt(args.get("timeout-ms") ?? "10000", 10);

  if (!url || !message || !output) {
    throw new Error("Missing required args: --url --message --output");
  }

  const transcript = {
    url,
    message,
    reply: null,
    receivedMessages: [],
  };

  await new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    let settled = false;

    const finish = async (error) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timeoutHandle);

      try {
        if (ws.readyState === WebSocket.OPEN) {
          ws.close();
        }
      } catch {
      }

      try {
        await fs.writeFile(output, JSON.stringify(transcript, null, 2) + "\n", "utf8");
      } catch (writeError) {
        if (!error) {
          error = writeError;
        }
      }

      if (error) {
        reject(error);
      } else {
        resolve();
      }
    };

    const timeoutHandle = setTimeout(() => {
      void finish(new Error(`Timed out waiting for websocket reply after ${timeoutMs} ms.`));
    }, timeoutMs);

    ws.addEventListener("open", () => {
      ws.send(message);
    });

    ws.addEventListener("message", async (event) => {
      const text = typeof event.data === "string" ? event.data : String(event.data);
      transcript.receivedMessages.push(text);
      if (!transcript.reply) {
        transcript.reply = text;
        await finish();
      }
    });

    ws.addEventListener("error", async () => {
      await finish(new Error("WebSocket client error."));
    });

    ws.addEventListener("close", async () => {
      if (!settled && !transcript.reply) {
        await finish(new Error("WebSocket closed before any reply was received."));
      }
    });
  });
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
