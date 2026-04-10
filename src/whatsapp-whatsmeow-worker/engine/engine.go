package engine

import (
	"context"
	"encoding/json"
	"fmt"
	"os"

	"github.com/openclaw/whatsapp-whatsmeow-worker/protocol"
)

// Config mirrors WhatsAppFirstPartyWorkerConfig from the .NET gateway.
type Config struct {
	Driver         string          `json:"driver"`
	StoragePath    string          `json:"storagePath"`
	MediaCachePath string          `json:"mediaCachePath"`
	HistorySync    bool            `json:"historySync"`
	Proxy          string          `json:"proxy"`
	Accounts       []AccountConfig `json:"accounts"`
}

// AccountConfig mirrors WhatsAppWorkerAccountConfig.
type AccountConfig struct {
	AccountID        string `json:"accountId"`
	SessionPath      string `json:"sessionPath"`
	DeviceName       string `json:"deviceName"`
	PairingMode      string `json:"pairingMode"`
	PhoneNumber      string `json:"phoneNumber"`
	SendReadReceipts bool   `json:"sendReadReceipts"`
	AckReaction      bool   `json:"ackReaction"`
	MediaCachePath   string `json:"mediaCachePath"`
	HistorySync      bool   `json:"historySync"`
	Proxy            string `json:"proxy"`
}

// SendRequest mirrors BridgeChannelSendRequest.
type SendRequest struct {
	ChannelID        string            `json:"channelId"`
	RecipientID      string            `json:"recipientId"`
	AccountID        string            `json:"accountId"`
	Text             string            `json:"text"`
	SessionID        string            `json:"sessionId"`
	ReplyToMessageID string            `json:"replyToMessageId"`
	Subject          string            `json:"subject"`
	Attachments      []MediaAttachment `json:"attachments"`
}

// MediaAttachment mirrors BridgeMediaAttachment.
type MediaAttachment struct {
	Type        string `json:"type"`
	URL         string `json:"url"`
	Caption     string `json:"caption"`
	MimeType    string `json:"mimeType"`
	FileName    string `json:"fileName"`
	GifPlayback bool   `json:"gifPlayback"`
}

// TypingRequest mirrors BridgeChannelTypingRequest.
type TypingRequest struct {
	ChannelID   string `json:"channelId"`
	RecipientID string `json:"recipientId"`
	AccountID   string `json:"accountId"`
	IsTyping    bool   `json:"isTyping"`
}

// ReceiptRequest mirrors BridgeChannelReceiptRequest.
type ReceiptRequest struct {
	ChannelID   string `json:"channelId"`
	MessageID   string `json:"messageId"`
	AccountID   string `json:"accountId"`
	RemoteJid   string `json:"remoteJid"`
	Participant string `json:"participant"`
}

// ReactionRequest mirrors BridgeChannelReactionRequest.
type ReactionRequest struct {
	ChannelID   string `json:"channelId"`
	MessageID   string `json:"messageId"`
	AccountID   string `json:"accountId"`
	Emoji       string `json:"emoji"`
	RemoteJid   string `json:"remoteJid"`
	Participant string `json:"participant"`
}

// ControlRequest mirrors BridgeChannelControlRequest.
type ControlRequest struct {
	ChannelID string `json:"channelId"`
}

// Engine manages multiple WhatsApp sessions via whatsmeow.
type Engine struct {
	config   *Config
	sessions map[string]*Session
	writer   *protocol.Writer
}

// NewEngine creates a new engine instance.
func NewEngine(writer *protocol.Writer) *Engine {
	return &Engine{
		sessions: make(map[string]*Session),
		writer:   writer,
	}
}

// Init configures the engine with the provided config.
func (e *Engine) Init(config *Config) {
	e.config = config
	accounts := config.Accounts
	if len(accounts) == 0 {
		accounts = []AccountConfig{{AccountID: "default"}}
	}

	for _, acc := range accounts {
		session := NewSession(acc, config, "whatsapp", e.writer)
		e.sessions[acc.AccountID] = session
	}
}

// Start connects all accounts and returns self IDs.
func (e *Engine) Start(ctx context.Context) (map[string]interface{}, error) {
	var selfIds []string

	for id, session := range e.sessions {
		if err := session.Start(ctx); err != nil {
			fmt.Fprintf(os.Stderr, "Failed to start account %s: %v\n", id, err)
			continue
		}
		if session.SelfID != "" {
			selfIds = append(selfIds, session.SelfID)
		}
	}

	result := map[string]interface{}{
		"ok":      true,
		"selfIds": selfIds,
	}
	if len(selfIds) > 0 {
		result["selfId"] = selfIds[0]
	}
	return result, nil
}

// Stop disconnects all accounts.
func (e *Engine) Stop() {
	for _, session := range e.sessions {
		session.Stop()
	}
}

// Send routes a message to the appropriate session.
func (e *Engine) Send(ctx context.Context, req *SendRequest) error {
	session, err := e.resolveSession(req.AccountID, req.RecipientID)
	if err != nil {
		return err
	}
	return session.Send(ctx, req)
}

// SendTyping sends a typing indicator.
func (e *Engine) SendTyping(ctx context.Context, req *TypingRequest) error {
	session, err := e.resolveSession(req.AccountID, req.RecipientID)
	if err != nil {
		return err
	}
	return session.SendTyping(ctx, req)
}

// SendReadReceipt marks a message as read.
func (e *Engine) SendReadReceipt(ctx context.Context, req *ReceiptRequest) error {
	session, err := e.resolveSession(req.AccountID, req.RemoteJid)
	if err != nil {
		return err
	}
	return session.SendReadReceipt(ctx, req)
}

// SendReaction sends an emoji reaction.
func (e *Engine) SendReaction(ctx context.Context, req *ReactionRequest) error {
	session, err := e.resolveSession(req.AccountID, req.RemoteJid)
	if err != nil {
		return err
	}
	return session.SendReaction(ctx, req)
}

// GetState returns internal state for debugging.
func (e *Engine) GetState() interface{} {
	accounts := make(map[string]interface{})
	for id, session := range e.sessions {
		accounts[id] = map[string]interface{}{
			"selfId":    session.SelfID,
			"connected": session.Connected,
		}
	}
	return map[string]interface{}{
		"driver":   "whatsmeow",
		"accounts": accounts,
	}
}

// ParseJSON is a helper to unmarshal raw JSON params into a typed struct.
func ParseJSON[T any](raw *json.RawMessage) (*T, error) {
	if raw == nil {
		var zero T
		return &zero, nil
	}
	var v T
	if err := json.Unmarshal(*raw, &v); err != nil {
		return nil, err
	}
	return &v, nil
}

func (e *Engine) resolveSession(accountID string, recipientJid string) (*Session, error) {
	if accountID != "" {
		if s, ok := e.sessions[accountID]; ok {
			return s, nil
		}
		return nil, fmt.Errorf("unknown WhatsApp account %q", accountID)
	}

	if len(e.sessions) == 1 {
		for _, s := range e.sessions {
			return s, nil
		}
	}

	session := e.defaultSession()
	if session == nil {
		return nil, fmt.Errorf("no WhatsApp sessions are configured")
	}

	return session, nil
}

func (e *Engine) defaultSession() *Session {
	if s, ok := e.sessions["default"]; ok {
		return s
	}
	for _, s := range e.sessions {
		return s
	}
	return nil
}
