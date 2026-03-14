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

function nowIso() {
  return new Date().toISOString();
}

function tryParseEnvelope(payload) {
  try {
    return JSON.parse(payload);
  } catch {
    return null;
  }
}

async function main() {
  const args = parseArgs(process.argv);
  const url = args.get("url");
  const prompt = args.get("prompt");
  const output = args.get("output");
  const sessionId = args.get("session-id");
  const timeoutMs = Number.parseInt(args.get("timeout-ms") ?? "20000", 10);

  if (!url || !prompt || !output || !sessionId) {
    throw new Error("Missing required args: --url --prompt --output --session-id");
  }

  const transcript = {
    url,
    sessionId,
    prompt,
    finalText: "",
    rawMessages: [],
    envelopes: [],
  };

  await new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    let settled = false;
    let chunkBuffer = "";
    let assistantDoneReceived = false;

    const finish = async (error) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timeoutHandle);

      transcript.finalText = transcript.finalText || chunkBuffer;

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
      void finish(new Error(`Timed out waiting for websocket turn after ${timeoutMs} ms.`));
    }, timeoutMs);

    ws.addEventListener("open", () => {
      ws.send(JSON.stringify({
        type: "user_message",
        text: prompt,
        sessionId,
        messageId: `msg-${Date.now()}`,
      }));
    });

    ws.addEventListener("message", async (event) => {
      const text = typeof event.data === "string" ? event.data : String(event.data);
      transcript.rawMessages.push({
        text,
        timestamp: nowIso(),
      });

      const envelope = tryParseEnvelope(text);
      if (!envelope) {
        transcript.finalText = text;
        await finish();
        return;
      }

      transcript.envelopes.push({
        ...envelope,
        timestamp: nowIso(),
      });

      switch (envelope.type) {
        case "assistant_chunk":
          chunkBuffer += String(envelope.text ?? "");
          break;
        case "assistant_message":
          chunkBuffer += String(envelope.text ?? "");
          transcript.finalText = chunkBuffer;
          await finish();
          return;
        case "assistant_done":
          transcript.finalText = chunkBuffer;
          assistantDoneReceived = true;
          break;
        case "typing_stop":
          if (assistantDoneReceived || transcript.finalText || chunkBuffer) {
            transcript.finalText = transcript.finalText || chunkBuffer;
            await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
            await finish();
            return;
          }
          break;
        case "error":
          await finish(new Error(String(envelope.text ?? "Received websocket error envelope.")));
          return;
        default:
          break;
      }
    });

    ws.addEventListener("error", async () => {
      await finish(new Error("WebSocket client error."));
    });

    ws.addEventListener("close", async () => {
      if (!settled && !transcript.finalText && !chunkBuffer) {
        await finish(new Error("WebSocket closed before the turn completed."));
      }
    });
  });
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
