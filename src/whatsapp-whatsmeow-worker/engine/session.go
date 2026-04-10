package engine

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"github.com/openclaw/whatsapp-whatsmeow-worker/protocol"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store/sqlstore"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/util/dbushelper"
	waProto "go.mau.fi/whatsmeow/binary/proto"

	_ "github.com/mattn/go-sqlite3"
	"google.golang.org/protobuf/proto"
)

// Session wraps a single whatsmeow client for one WhatsApp account.
type Session struct {
	AccountID  string
	SelfID     string
	Connected  bool
	ChannelID  string

	config       AccountConfig
	globalConfig *Config
	client       *whatsmeow.Client
	writer       *protocol.Writer
	stopped      bool
}

// NewSession creates a new whatsmeow session wrapper.
func NewSession(acc AccountConfig, global *Config, channelID string, writer *protocol.Writer) *Session {
	return &Session{
		AccountID:    acc.AccountID,
		ChannelID:    channelID,
		config:       acc,
		globalConfig: global,
		writer:       writer,
	}
}

// Start connects the whatsmeow client.
func (s *Session) Start(ctx context.Context) error {
	sessionPath := s.config.SessionPath
	if sessionPath == "" {
		sessionPath = filepath.Join(s.globalConfig.StoragePath, "session", s.AccountID)
	}
	if err := os.MkdirAll(sessionPath, 0o755); err != nil {
		return fmt.Errorf("failed to create session directory: %w", err)
	}

	dbPath := filepath.Join(sessionPath, "whatsmeow.db")
	container, err := sqlstore.New("sqlite3", fmt.Sprintf("file:%s?_foreign_keys=on", dbPath), waLog.Noop)
	if err != nil {
		return fmt.Errorf("failed to open session store: %w", err)
	}

	deviceStore, err := container.GetFirstDevice()
	if err != nil {
		return fmt.Errorf("failed to get device: %w", err)
	}

	s.client = whatsmeow.NewClient(deviceStore, waLog.Noop)
	s.client.AddEventHandler(s.handleEvent)

	if s.client.Store.ID == nil {
		if s.config.PairingMode == "pairing_code" && s.config.PhoneNumber != "" {
			if err := s.client.Connect(); err != nil {
				return fmt.Errorf("connect failed: %w", err)
			}
			code, err := s.client.PairPhone(s.config.PhoneNumber, true, whatsmeow.PairClientChrome, "Chrome (Linux)")
			if err != nil {
				s.writer.SendNotification("channel_auth_event", map[string]interface{}{
					"channelId": s.ChannelID,
					"state":     "error",
					"data":      fmt.Sprintf("Pairing code request failed: %v", err),
					"accountId": s.AccountID,
				})
				return err
			}
			s.writer.SendNotification("channel_auth_event", map[string]interface{}{
				"channelId": s.ChannelID,
				"state":     "pairing_code",
				"data":      code,
				"accountId": s.AccountID,
			})
		} else {
			qrChan, _ := s.client.GetQRChannel(ctx)
			if err := s.client.Connect(); err != nil {
				return fmt.Errorf("connect failed: %w", err)
			}
			go s.processQREvents(qrChan)
		}
	} else {
		if err := s.client.Connect(); err != nil {
			return fmt.Errorf("reconnect failed: %w", err)
		}
	}

	return nil
}

// Stop disconnects the client.
func (s *Session) Stop() {
	s.stopped = true
	if s.client != nil {
		s.client.Disconnect()
	}
	s.Connected = false
}

// Send sends a message with optional media attachments.
func (s *Session) Send(ctx context.Context, req *SendRequest) error {
	if s.client == nil {
		return fmt.Errorf("session not connected")
	}

	jid, err := types.ParseJID(req.RecipientID)
	if err != nil {
		return fmt.Errorf("invalid JID: %w", err)
	}

	// Handle media attachments
	if len(req.Attachments) > 0 {
		return s.sendMedia(ctx, jid, req)
	}

	// Text-only message
	msg := &waProto.Message{
		Conversation: proto.String(req.Text),
	}

	_, err = s.client.SendMessage(ctx, jid, msg)
	return err
}

func (s *Session) sendMedia(ctx context.Context, jid types.JID, req *SendRequest) error {
	att := req.Attachments[0]

	data, err := downloadMedia(att.URL)
	if err != nil {
		return fmt.Errorf("failed to download media: %w", err)
	}

	mimeType := att.MimeType
	if mimeType == "" {
		mimeType = "application/octet-stream"
	}

	caption := att.Caption
	if caption == "" && req.Text != "" {
		caption = req.Text
	}

	var msg *waProto.Message

	switch att.Type {
	case "image":
		uploaded, err := s.client.Upload(ctx, data, whatsmeow.MediaImage)
		if err != nil {
			return fmt.Errorf("upload image failed: %w", err)
		}
		msg = &waProto.Message{
			ImageMessage: &waProto.ImageMessage{
				URL:           &uploaded.URL,
				DirectPath:    &uploaded.DirectPath,
				MediaKey:      uploaded.MediaKey,
				FileEncSHA256: uploaded.FileEncSHA256,
				FileSHA256:    uploaded.FileSHA256,
				FileLength:    proto.Uint64(uint64(len(data))),
				Mimetype:      &mimeType,
				Caption:       nilIfEmpty(caption),
			},
		}

	case "video":
		uploaded, err := s.client.Upload(ctx, data, whatsmeow.MediaVideo)
		if err != nil {
			return fmt.Errorf("upload video failed: %w", err)
		}
		msg = &waProto.Message{
			VideoMessage: &waProto.VideoMessage{
				URL:           &uploaded.URL,
				DirectPath:    &uploaded.DirectPath,
				MediaKey:      uploaded.MediaKey,
				FileEncSHA256: uploaded.FileEncSHA256,
				FileSHA256:    uploaded.FileSHA256,
				FileLength:    proto.Uint64(uint64(len(data))),
				Mimetype:      &mimeType,
				Caption:       nilIfEmpty(caption),
				GifPlayback:   proto.Bool(att.GifPlayback),
			},
		}

	case "audio":
		// Ensure voice note mime type
		if mimeType == "audio/ogg" {
			mimeType = "audio/ogg; codecs=opus"
		}
		uploaded, err := s.client.Upload(ctx, data, whatsmeow.MediaAudio)
		if err != nil {
			return fmt.Errorf("upload audio failed: %w", err)
		}
		msg = &waProto.Message{
			AudioMessage: &waProto.AudioMessage{
				URL:           &uploaded.URL,
				DirectPath:    &uploaded.DirectPath,
				MediaKey:      uploaded.MediaKey,
				FileEncSHA256: uploaded.FileEncSHA256,
				FileSHA256:    uploaded.FileSHA256,
				FileLength:    proto.Uint64(uint64(len(data))),
				Mimetype:      &mimeType,
				Ptt:           proto.Bool(true),
			},
		}

	case "sticker":
		uploaded, err := s.client.Upload(ctx, data, whatsmeow.MediaImage)
		if err != nil {
			return fmt.Errorf("upload sticker failed: %w", err)
		}
		stickerMime := mimeType
		if stickerMime == "application/octet-stream" {
			stickerMime = "image/webp"
		}
		msg = &waProto.Message{
			StickerMessage: &waProto.StickerMessage{
				URL:           &uploaded.URL,
				DirectPath:    &uploaded.DirectPath,
				MediaKey:      uploaded.MediaKey,
				FileEncSHA256: uploaded.FileEncSHA256,
				FileSHA256:    uploaded.FileSHA256,
				FileLength:    proto.Uint64(uint64(len(data))),
				Mimetype:      &stickerMime,
			},
		}

	default: // document
		uploaded, err := s.client.Upload(ctx, data, whatsmeow.MediaDocument)
		if err != nil {
			return fmt.Errorf("upload document failed: %w", err)
		}
		fileName := att.FileName
		if fileName == "" {
			fileName = "document"
		}
		msg = &waProto.Message{
			DocumentMessage: &waProto.DocumentMessage{
				URL:           &uploaded.URL,
				DirectPath:    &uploaded.DirectPath,
				MediaKey:      uploaded.MediaKey,
				FileEncSHA256: uploaded.FileEncSHA256,
				FileSHA256:    uploaded.FileSHA256,
				FileLength:    proto.Uint64(uint64(len(data))),
				Mimetype:      &mimeType,
				FileName:      &fileName,
				Caption:       nilIfEmpty(caption),
			},
		}
	}

	_, err := s.client.SendMessage(ctx, jid, msg)
	return err
}

// SendTyping sends a composing/paused presence update.
func (s *Session) SendTyping(ctx context.Context, req *TypingRequest) error {
	if s.client == nil {
		return nil
	}

	jid, err := types.ParseJID(req.RecipientID)
	if err != nil {
		return nil
	}

	media := types.ChatPresenceComposing
	if !req.IsTyping {
		media = types.ChatPresencePaused
	}

	return s.client.SendChatPresence(jid, media, "")
}

// SendReadReceipt marks a message as read.
func (s *Session) SendReadReceipt(ctx context.Context, req *ReceiptRequest) error {
	if s.client == nil || req.RemoteJid == "" {
		return nil
	}

	chatJid, err := types.ParseJID(req.RemoteJid)
	if err != nil {
		return nil
	}

	var senderJid types.JID
	if req.Participant != "" {
		senderJid, _ = types.ParseJID(req.Participant)
	}

	ids := []types.MessageID{req.MessageID}
	return s.client.MarkRead(ids, time.Now(), chatJid, senderJid)
}

// SendReaction sends an emoji reaction to a message.
func (s *Session) SendReaction(ctx context.Context, req *ReactionRequest) error {
	if s.client == nil || req.RemoteJid == "" {
		return nil
	}

	chatJid, err := types.ParseJID(req.RemoteJid)
	if err != nil {
		return nil
	}

	var senderJid types.JID
	if req.Participant != "" {
		senderJid, _ = types.ParseJID(req.Participant)
	}

	msg := &waProto.Message{
		ReactionMessage: &waProto.ReactionMessage{
			Key: &waProto.MessageKey{
				RemoteJid: &req.RemoteJid,
				Id:        &req.MessageID,
				FromMe:    proto.Bool(false),
				Participant: func() *string {
					if req.Participant != "" {
						return &req.Participant
					}
					return nil
				}(),
			},
			Text:              &req.Emoji,
			SenderTimestampMs: proto.Int64(time.Now().UnixMilli()),
		},
	}

	_, err = s.client.SendMessage(ctx, chatJid, msg)
	return err
}

func (s *Session) processQREvents(qrChan <-chan whatsmeow.QRChannelItem) {
	for evt := range qrChan {
		switch evt.Event {
		case "code":
			s.writer.SendNotification("channel_auth_event", map[string]interface{}{
				"channelId": s.ChannelID,
				"state":     "qr_code",
				"data":      evt.Code,
				"accountId": s.AccountID,
			})
		case "timeout":
			s.writer.SendNotification("channel_auth_event", map[string]interface{}{
				"channelId": s.ChannelID,
				"state":     "error",
				"data":      "QR code timed out. Restart to try again.",
				"accountId": s.AccountID,
			})
		case "success":
			// Connection event will fire separately
		}
	}
}

func (s *Session) handleEvent(rawEvt interface{}) {
	switch evt := rawEvt.(type) {
	case *events.Connected:
		s.Connected = true
		if s.client.Store.ID != nil {
			s.SelfID = s.client.Store.ID.User + "@s.whatsapp.net"
		}
		s.writer.SendNotification("channel_auth_event", map[string]interface{}{
			"channelId": s.ChannelID,
			"state":     "connected",
			"data":      s.SelfID,
			"accountId": s.AccountID,
		})

	case *events.Disconnected:
		s.Connected = false
		s.writer.SendNotification("channel_auth_event", map[string]interface{}{
			"channelId": s.ChannelID,
			"state":     "disconnected",
			"data":      "Disconnected. Reconnecting...",
			"accountId": s.AccountID,
		})

	case *events.LoggedOut:
		s.Connected = false
		s.writer.SendNotification("channel_auth_event", map[string]interface{}{
			"channelId": s.ChannelID,
			"state":     "error",
			"data":      fmt.Sprintf("Logged out (reason: %d). Re-pairing required.", evt.Reason),
			"accountId": s.AccountID,
		})

	case *events.Message:
		s.handleInboundMessage(evt)
	}
}

func (s *Session) handleInboundMessage(evt *events.Message) {
	if evt.Info.IsFromMe {
		return
	}
	if evt.Info.Chat.Server == "broadcast" {
		return
	}

	text := ""
	if evt.Message.GetConversation() != "" {
		text = evt.Message.GetConversation()
	} else if ext := evt.Message.GetExtendedTextMessage(); ext != nil {
		text = ext.GetText()
	}

	isGroup := evt.Info.IsGroup
	senderId := evt.Info.Sender.String()

	msg := map[string]interface{}{
		"channelId": s.ChannelID,
		"accountId": s.AccountID,
		"senderId":  senderId,
		"text":      text,
		"sessionId": func() string {
			if isGroup {
				return "whatsapp:group:" + evt.Info.Chat.String()
			}
			return senderId
		}(),
		"messageId": evt.Info.ID,
		"isGroup":   isGroup,
	}

	if isGroup {
		msg["groupId"] = evt.Info.Chat.String()
	}

	if evt.Info.PushName != "" {
		msg["senderName"] = evt.Info.PushName
	}

	// Extract context info (mentions, quoted message) with nil safety
	if ext := evt.Message.GetExtendedTextMessage(); ext != nil {
		if ci := ext.GetContextInfo(); ci != nil {
			if ci.GetStanzaId() != "" {
				msg["replyToMessageId"] = ci.GetStanzaId()
			}
			if len(ci.GetMentionedJid()) > 0 {
				msg["mentionedIds"] = ci.GetMentionedJid()
			}
		}
	}

	// Inbound media download
	mediaInfo := s.downloadInboundMedia(evt)
	if mediaInfo != nil {
		for k, v := range mediaInfo {
			msg[k] = v
		}
	}

	// Also extract captions from media messages
	if text == "" {
		if cap := evt.Message.GetImageMessage().GetCaption(); cap != "" {
			msg["text"] = cap
		} else if cap := evt.Message.GetVideoMessage().GetCaption(); cap != "" {
			msg["text"] = cap
		} else if cap := evt.Message.GetDocumentMessage().GetCaption(); cap != "" {
			msg["text"] = cap
		}
	}

	s.writer.SendNotification("channel_message", msg)
}

func (s *Session) downloadInboundMedia(evt *events.Message) map[string]interface{} {
	raw := evt.Message
	if raw == nil {
		return nil
	}

	var mediaType string
	var downloadable whatsmeow.DownloadableMessage
	var mimeType string
	var fileName string

	switch {
	case raw.GetImageMessage() != nil:
		mediaType = "image"
		downloadable = raw.GetImageMessage()
		mimeType = raw.GetImageMessage().GetMimetype()
	case raw.GetVideoMessage() != nil:
		mediaType = "video"
		downloadable = raw.GetVideoMessage()
		mimeType = raw.GetVideoMessage().GetMimetype()
	case raw.GetAudioMessage() != nil:
		mediaType = "audio"
		downloadable = raw.GetAudioMessage()
		mimeType = raw.GetAudioMessage().GetMimetype()
	case raw.GetDocumentMessage() != nil:
		mediaType = "document"
		downloadable = raw.GetDocumentMessage()
		mimeType = raw.GetDocumentMessage().GetMimetype()
		fileName = raw.GetDocumentMessage().GetFileName()
	case raw.GetStickerMessage() != nil:
		mediaType = "sticker"
		downloadable = raw.GetStickerMessage()
		mimeType = raw.GetStickerMessage().GetMimetype()
	default:
		return nil
	}

	data, err := s.client.Download(downloadable)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to download %s media: %v\n", mediaType, err)
		return map[string]interface{}{
			"mediaType":     mediaType,
			"mediaMimeType": mimeType,
			"mediaFileName": fileName,
		}
	}

	// Save to cache
	cachePath := s.config.MediaCachePath
	if cachePath == "" {
		cachePath = s.globalConfig.MediaCachePath
	}
	if cachePath == "" {
		cachePath = filepath.Join(s.globalConfig.StoragePath, "media-cache")
	}
	_ = os.MkdirAll(cachePath, 0o755)

	ext := extensionForMime(mimeType)
	outName := fmt.Sprintf("%s%s", evt.Info.ID, ext)
	outPath := filepath.Join(cachePath, outName)
	if err := os.WriteFile(outPath, data, 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "Failed to save media to cache: %v\n", err)
	}

	if fileName == "" {
		fileName = outName
	}

	return map[string]interface{}{
		"mediaType":     mediaType,
		"mediaUrl":      "file://" + outPath,
		"mediaMimeType": mimeType,
		"mediaFileName": fileName,
	}
}

func downloadMedia(url string) ([]byte, error) {
	if len(url) > 7 && url[:7] == "file://" {
		return os.ReadFile(url[7:])
	}

	resp, err := http.Get(url)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("HTTP %d fetching media", resp.StatusCode)
	}

	return io.ReadAll(resp.Body)
}

func extensionForMime(mime string) string {
	switch mime {
	case "image/jpeg":
		return ".jpg"
	case "image/png":
		return ".png"
	case "image/webp":
		return ".webp"
	case "image/gif":
		return ".gif"
	case "video/mp4":
		return ".mp4"
	case "audio/ogg", "audio/ogg; codecs=opus":
		return ".ogg"
	case "audio/mpeg":
		return ".mp3"
	case "audio/mp4":
		return ".m4a"
	case "application/pdf":
		return ".pdf"
	default:
		return ".bin"
	}
}

func nilIfEmpty(s string) *string {
	if s == "" {
		return nil
	}
	return &s
}
