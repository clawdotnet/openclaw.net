import { BaileysSession } from "./session.mjs";

export class BaileysEngine {
  constructor() {
    this.sessions = new Map();
    this.config = null;
    this.channelId = "whatsapp";
  }

  init(config) {
    this.config = config;
    const accounts = config.accounts || [];
    if (accounts.length === 0) {
      accounts.push({ accountId: "default" });
    }

    for (const account of accounts) {
      const session = new BaileysSession(account, config, this.channelId);
      this.sessions.set(account.accountId || "default", session);
    }
  }

  async start() {
    const selfIds = [];

    for (const [accountId, session] of this.sessions) {
      try {
        await session.start();
        if (session.selfId) {
          selfIds.push(session.selfId);
        }
      } catch (err) {
        console.error(`Failed to start account ${accountId}: ${err?.message}`);
      }
    }

    return {
      ok: true,
      selfId: selfIds[0] || null,
      selfIds,
    };
  }

  async stop() {
    for (const session of this.sessions.values()) {
      try {
        await session.stop();
      } catch (err) {
        console.error(
          `Failed to stop account ${session.accountId}: ${err?.message}`
        );
      }
    }
    return { stopped: true };
  }

  async send(request) {
    const session = this._resolveSession(request.accountId, request.recipientId);
    await session.send(request);
    return { sent: true };
  }

  async sendTyping(request) {
    const session = this._resolveSession(request.accountId, request.recipientId);
    await session.sendTyping(request.recipientId, request.isTyping ?? true);
    return { accepted: true };
  }

  async sendReadReceipt(request) {
    const session = request.remoteJid
      ? this._resolveSession(request.accountId, request.remoteJid)
      : this._resolveSession(request.accountId, null);
    await session.sendReadReceipt(request.messageId, request.remoteJid, request.participant);
    return { accepted: true };
  }

  async sendReaction(request) {
    const session = request.remoteJid
      ? this._resolveSession(request.accountId, request.remoteJid)
      : this._resolveSession(request.accountId, null);
    await session.sendReaction(request.messageId, request.emoji, request.remoteJid, request.participant);
    return { accepted: true };
  }

  getState() {
    const accounts = {};
    for (const [id, session] of this.sessions) {
      accounts[id] = {
        selfId: session.selfId,
        connected: session.sock !== null,
      };
    }
    return { driver: "baileys", accounts };
  }

  _resolveSession(accountId, recipientJid) {
    if (accountId) {
      const session = this.sessions.get(accountId);
      if (!session) {
        throw new Error(`Unknown WhatsApp account '${accountId}'`);
      }
      return session;
    }

    if (this.sessions.size === 1) {
      return this.sessions.values().next().value;
    }

    // Multi-account requests without an explicit accountId fall back to the default session.
    return this._getDefaultSession();
  }

  _getDefaultSession() {
    return (
      this.sessions.get("default") || this.sessions.values().next().value
    );
  }
}
