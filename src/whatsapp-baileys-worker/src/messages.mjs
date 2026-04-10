import { isJidGroup, jidNormalizedUser } from "@whiskeysockets/baileys";

export function mapInboundMessage(msg, mediaInfo, accountId) {
  const raw = msg.message;
  if (!raw) return null;

  const from = msg.key?.remoteJid;
  if (!from) return null;

  // Skip status broadcasts
  if (from === "status@broadcast") return null;

  const isGroup = isJidGroup(from);
  const senderId = isGroup
    ? msg.key?.participant || from
    : from;

  // Extract text from various message types
  let text = "";
  if (raw.conversation) {
    text = raw.conversation;
  } else if (raw.extendedTextMessage?.text) {
    text = raw.extendedTextMessage.text;
  } else if (raw.imageMessage?.caption) {
    text = raw.imageMessage.caption;
  } else if (raw.videoMessage?.caption) {
    text = raw.videoMessage.caption;
  } else if (raw.documentMessage?.caption) {
    text = raw.documentMessage.caption;
  }

  // Extract reply context
  const contextInfo =
    raw.extendedTextMessage?.contextInfo ||
    raw.imageMessage?.contextInfo ||
    raw.videoMessage?.contextInfo ||
    raw.audioMessage?.contextInfo ||
    raw.documentMessage?.contextInfo;

  const replyToMessageId = contextInfo?.stanzaId || null;
  const mentionedIds = contextInfo?.mentionedJid || null;

  // Contact name from push name
  const senderName = msg.pushName || null;

  const result = {
    senderId: jidNormalizedUser(senderId),
    accountId,
    senderName,
    text,
    sessionId: isGroup ? `whatsapp:group:${from}` : jidNormalizedUser(senderId),
    messageId: msg.key?.id || null,
    replyToMessageId,
    isGroup,
    groupId: isGroup ? from : null,
    groupName: null, // filled in by caller if available
    mentionedIds: mentionedIds?.length > 0 ? mentionedIds.map(jidNormalizedUser) : null,
  };

  // Merge media info if present
  if (mediaInfo) {
    result.mediaType = mediaInfo.mediaType;
    result.mediaUrl = mediaInfo.mediaUrl;
    result.mediaMimeType = mediaInfo.mediaMimeType;
    result.mediaFileName = mediaInfo.mediaFileName;
  }

  return result;
}

export function buildOutboundContent(request) {
  const { text, attachments, replyToMessageId } = request;

  const options = {};
  if (replyToMessageId) {
    options.quoted = { key: { id: replyToMessageId } };
  }

  // No attachments — send text only
  if (!attachments || attachments.length === 0) {
    return { content: { text: text || "" }, options };
  }

  // First attachment determines the message type
  const att = attachments[0];
  // Note: the actual buffer is prepared by the caller via media.mjs
  // This function returns the structure that session.mjs uses after downloading
  return { attachment: att, text, options };
}
