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

async function main() {
  const args = parseArgs(process.argv);
  const url = args.get("url");
  const prompt = args.get("prompt");
  const output = args.get("output");
  const decision = (args.get("decision") ?? "approve").toLowerCase();
  const timeoutMs = Number.parseInt(args.get("timeout-ms") ?? "20000", 10);

  if (!url || !prompt || !output) {
    throw new Error("Missing required args: --url --prompt --output");
  }

  if (!["approve", "deny", "wait"].includes(decision)) {
    throw new Error("Expected --decision approve|deny|wait");
  }

  const transcript = {
    url,
    prompt,
    decision,
    approvalPromptText: null,
    approvalId: null,
    approvalCommand: null,
    approvalAckText: null,
    finalText: null,
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
      void finish(new Error(`Timed out waiting for approval flow after ${timeoutMs} ms.`));
    }, timeoutMs);

    ws.addEventListener("open", () => {
      ws.send(prompt);
    });

    ws.addEventListener("message", async (event) => {
      const text = typeof event.data === "string" ? event.data : String(event.data);
      transcript.receivedMessages.push({
        text,
        timestamp: nowIso(),
      });

      if (!transcript.approvalId && text.includes("Tool approval required.")) {
        transcript.approvalPromptText = text;
        const match = text.match(/- id:\s*(\S+)/);
        if (!match) {
          await finish(new Error(`Approval prompt did not include an approval id: ${text}`));
          return;
        }

        transcript.approvalId = match[1];
        if (decision !== "wait") {
          const approved = decision === "approve" ? "yes" : "no";
          transcript.approvalCommand = `/approve ${transcript.approvalId} ${approved}`;
          ws.send(transcript.approvalCommand);
        }
        return;
      }

      if (transcript.approvalId && text.includes(`Tool approval recorded: ${transcript.approvalId} = `)) {
        transcript.approvalAckText = text;
        return;
      }

      if (transcript.approvalId
        && !transcript.finalText
        && !text.includes("Tool approval required.")
        && !text.includes(`Tool approval recorded: ${transcript.approvalId} = `)) {
        transcript.finalText = text;
        clearTimeout(timeoutHandle);
        await finish();
      }
    });

    ws.addEventListener("error", async () => {
      await finish(new Error("WebSocket client error."));
    });

    ws.addEventListener("close", async () => {
      if (!settled && !transcript.finalText) {
        await finish(new Error("WebSocket closed before the approval flow completed."));
      }
    });
  });
}

main().catch(async (error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
