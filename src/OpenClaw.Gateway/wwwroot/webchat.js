// Set up marked to use highlight.js
marked.setOptions({
    highlight: function (code, lang) {
        const language = hljs.getLanguage(lang) ? lang : 'plaintext';
        return hljs.highlight(code, { language }).value;
    },
    langPrefix: 'hljs language-'
});

const chatWrapper = document.getElementById('chat-wrapper');
const chatContainer = document.getElementById('chat-container');
const messageInput = document.getElementById('message-input');
const sendButton = document.getElementById('send-button');
const attachButton = document.getElementById('attach-button');
const imageInput = document.getElementById('image-input');
const tokenInput = document.getElementById('token-input');
const rememberToken = document.getElementById('remember-token');
const doctorButton = document.getElementById('doctor-button');
const headerRuntimeBadge = document.getElementById('header-runtime-badge');
const connectionPill = document.getElementById('connection-pill');
const connectionPillLabel = connectionPill?.querySelector('.conn-pill__label') || connectionPill;
const connectionBanner = document.getElementById('connection-banner');
const connectionBannerText = document.getElementById('connection-banner-text');
const connectionBannerAction = document.getElementById('connection-banner-action');
const authBanner = document.getElementById('auth-banner');
const authBannerAction = document.getElementById('auth-banner-action');
const settingsButton = document.getElementById('settings-button');
const settingsDrawer = document.getElementById('settings-drawer');
const settingsBackdrop = document.getElementById('settings-backdrop');
const settingsCloseButton = document.getElementById('settings-close-button');
const reconnectButton = document.getElementById('reconnect-button');
const themeToggleButton = document.getElementById('theme-toggle');
const themeIconLight = document.getElementById('theme-icon-light');
const themeIconDark = document.getElementById('theme-icon-dark');
const themeToggleGroup = document.querySelectorAll('[data-theme-value]');
const firstRunCard = document.getElementById('first-run-card');
const firstRunConnection = document.getElementById('first-run-connection');
const firstRunProvider = document.getElementById('first-run-provider');
const firstRunConnectProvider = document.getElementById('first-run-connect-provider');
const firstRunUseDefaults = document.getElementById('first-run-use-defaults');
const firstRunAdvanced = document.getElementById('first-run-advanced');
const drawerConnectProvider = document.getElementById('drawer-connect-provider');
const providerStatus = document.getElementById('provider-status');
const providerConfigSummary = document.getElementById('provider-config-summary');
const providerModal = document.getElementById('provider-modal');
const providerSelect = document.getElementById('provider-select');
const providerKeyInput = document.getElementById('provider-key-input');
const providerBaseUrlField = document.getElementById('provider-base-url-field');
const providerBaseUrlInput = document.getElementById('provider-base-url-input');
const providerModelInput = document.getElementById('provider-model-input');
const providerModalClose = document.getElementById('provider-modal-close');
const providerSaveButton = document.getElementById('provider-save-button');
const providerSkipButton = document.getElementById('provider-skip-button');
const rawRuntimeState = document.getElementById('raw-runtime-state');
const advancedClearToken = document.getElementById('advanced-clear-token');
const advancedResetUi = document.getElementById('advanced-reset-ui');
const workspaceEl = document.getElementById('workspace');
const statusConnection = document.getElementById('status-connection');
const statusAuth = document.getElementById('status-auth');
const statusSurface = document.getElementById('status-surface');
const statusCapabilities = document.getElementById('status-capabilities');
const statusCanvas = document.getElementById('status-canvas');
const statusLive = document.getElementById('status-live');
const authSummaryMode = document.getElementById('auth-summary-mode');
const authSummarySurface = document.getElementById('auth-summary-surface');
const authSummaryRemember = document.getElementById('auth-summary-remember');
const authSummaryCapabilities = document.getElementById('auth-summary-capabilities');
const modeSelect = document.getElementById('mode-select');
const liveProviderSelect = document.getElementById('live-provider-select');
const speechProviderSelect = document.getElementById('speech-provider-select');
const voiceIdInput = document.getElementById('voice-id-input');
const liveToolbar = document.getElementById('live-toolbar');
const liveConnectButton = document.getElementById('live-connect-button');
const liveMicButton = document.getElementById('live-mic-button');
const liveInterruptButton = document.getElementById('live-interrupt-button');
const liveStatusBadge = document.getElementById('live-status-badge');
const liveMicStatus = document.getElementById('live-mic-status');
const liveTimeline = document.getElementById('live-timeline');
const chatStateBar = document.getElementById('chat-state-bar');
const typingRow = document.getElementById('typing-row');
const emptyState = document.getElementById('empty-state');
const canvasPanel = document.getElementById('canvas-panel');
const canvasStatus = document.getElementById('canvas-status');
const canvasHideButton = document.getElementById('canvas-hide-button');
const canvasResetButton = document.getElementById('canvas-reset-button');
const canvasSnapshotButton = document.getElementById('canvas-snapshot-button');
const canvasMetaSurface = document.getElementById('canvas-meta-surface');
const canvasMetaCount = document.getElementById('canvas-meta-count');
const canvasMetaProtocol = document.getElementById('canvas-meta-protocol');
const canvasMetaUpdated = document.getElementById('canvas-meta-updated');
const canvasEmpty = document.getElementById('canvas-empty');
const canvasHtmlFrame = document.getElementById('canvas-html-frame');
const a2uiSurfaces = document.getElementById('a2ui-surfaces');
const a2uiTabs = document.getElementById('a2ui-tabs');
const a2uiSurfaceHost = document.getElementById('a2ui-surface-host');
const canvasDiagnostics = document.getElementById('canvas-diagnostics');
const attachmentSummary = document.getElementById('attachment-summary');
const attachmentCount = document.getElementById('attachment-count');
const attachmentList = document.getElementById('attachment-list');
const clearAttachmentsButton = document.getElementById('clear-attachments-button');
const composerDisabledReason = document.getElementById('composer-disabled-reason');
// New panel buttons and elements
const sessionToggleBtn = document.getElementById('sessions-toggle-btn');
const sessionSidebar = document.getElementById('session-sidebar');
const sessionSidebarClose = document.getElementById('session-sidebar-close');
const newChatBtn = document.getElementById('new-chat-btn');
const sessionList = document.getElementById('session-list');
const sessionSearchInput = document.getElementById('session-search-input');
const clearAllSessionsBtn = document.getElementById('clear-all-sessions-btn');
const historyBanner = document.getElementById('history-banner');
const historyBannerText = document.getElementById('history-banner-text');
const historyBannerClose = document.getElementById('history-banner-close');
const fileChips = document.getElementById('file-chips');
const attachFileBtn = document.getElementById('attach-file-btn');
const fileInput = document.getElementById('file-input');
const approvalModal = document.getElementById('approval-modal');
const approvalToolName = document.getElementById('approval-tool-name');
const approvalIdEl = document.getElementById('approval-id');
const approvalRisk = document.getElementById('approval-risk');
const approvalArgs = document.getElementById('approval-args');
const approvalApproveButton = document.getElementById('approval-approve-button');
const approvalDenyButton = document.getElementById('approval-deny-button');
const approvalCloseButton = document.getElementById('approval-close-button');
const doctorModal = document.getElementById('doctor-modal');
const doctorOutput = document.getElementById('doctor-output');
const doctorCloseButton = document.getElementById('doctor-close-button');
const doctorCopyButton = document.getElementById('doctor-copy-button');
const doctorAddChatButton = document.getElementById('doctor-add-chat-button');
const TOKEN_KEY_PERSIST = 'openclaw_token';
const TOKEN_KEY_SESSION = 'openclaw_token_session';
const OIDC_TOKEN_KEY  = 'openclaw_oidc_token';  // sessionStorage — JWT is sensitive
const OIDC_CONFIG_KEY = 'openclaw_oidc_config'; // localStorage — non-secret (authority + clientId)
const OIDC_STATE_KEY  = 'openclaw_oidc_state';  // sessionStorage — CSRF nonce
const OIDC_VERIFIER_KEY = 'openclaw_oidc_verifier'; // sessionStorage — PKCE verifier
const THEME_KEY = 'openclaw_theme';
const PROVIDER_SETUP_KEY = 'openclaw_provider_setup';
const FIRST_RUN_DISMISSED_KEY = 'openclaw_first_run_dismissed';
const AUTH_CLIENT_MODE_KEY = 'openclaw_auth_client_mode'; // 'token' | 'oidc'
const CURRENT_SESSION_KEY = 'openclaw_current_session_id';

let ws = null;
let liveWs = null;
let activeResponseDiv = null;
let activeRawContent = "";
let isAwaitingResponse = false;
let reconnectAttempts = 0;
let reconnectTimer = null;
let streamRenderTimer = null;
let liveResponseHasAudio = false;
let liveAudioQueue = [];
let liveAudioPlaying = false;
let liveMediaStream = null;
let liveAudioContext = null;
let liveAudioSource = null;
let liveAudioProcessor = null;
let canvasSessionId = null;
let canvasEventSequence = 0;
let latestChatState = null;
let connectionState = 'connecting';
let latestDoctorText = '';
let lastFocusedBeforeModal = null;
const approvalQueue = [];
let activeApproval = null;
const canvasSurfaces = new Map();
let activeCanvasSurfaceId = 'main';
let canvasHtmlUpdatedAt = null;
// Session management state
let currentSessionId = localStorage.getItem(CURRENT_SESSION_KEY) || null;
let isViewingHistory = false;
let allSessions = [];
let pendingFileUrls = []; // non-image files queued for upload

const WEBCHAT_CONFIG = {
    streamRenderDebounceMs: Math.max(20, Number(window.OPENCLAW_WEBCHAT_CONFIG?.streamRenderDebounceMs ?? 120)),
    initialReconnectDelayMs: Math.max(250, Number(window.OPENCLAW_WEBCHAT_CONFIG?.initialReconnectDelayMs ?? 1000)),
    maxReconnectDelayMs: Math.max(1000, Number(window.OPENCLAW_WEBCHAT_CONFIG?.maxReconnectDelayMs ?? 30000)),
    reconnectBackoffFactor: Math.max(1.1, Number(window.OPENCLAW_WEBCHAT_CONFIG?.reconnectBackoffFactor ?? 2)),
    maxReconnectAttempts: Math.max(0, Number(window.OPENCLAW_WEBCHAT_CONFIG?.maxReconnectAttempts ?? 0)),
    retryOnAuthCloseCodes: Boolean(window.OPENCLAW_WEBCHAT_CONFIG?.retryOnAuthCloseCodes ?? false)
};

function getStoredToken() {
    return sessionStorage.getItem(TOKEN_KEY_SESSION) || localStorage.getItem(TOKEN_KEY_PERSIST) || '';
}

function getCurrentToken() {
    if (getClientAuthMode() === 'oidc') {
        return sessionStorage.getItem(OIDC_TOKEN_KEY) || '';
    }
    return tokenInput.value.trim() || getStoredToken();
}

function persistToken(token) {
    if (!token) return;
    sessionStorage.setItem(TOKEN_KEY_SESSION, token);
    if (rememberToken.checked) {
        localStorage.setItem(TOKEN_KEY_PERSIST, token);
    } else {
        localStorage.removeItem(TOKEN_KEY_PERSIST);
    }
}

// ── Admin panel helpers ─────────────────────────────────────────────────────
function getBasePath() {
    const path = window.location.pathname;
    const lastSlash = path.lastIndexOf('/');
    return lastSlash > 0 ? path.substring(0, lastSlash) : '';
}

async function getAuthHeaders() {
    const token = getCurrentToken();
    const headers = {};
    if (token && token !== 'bypass') {
        headers.Authorization = 'Bearer ' + token;
    }
    const csrfToken = latestChatState?.csrfToken;
    if (csrfToken) {
        headers['X-CSRF-Token'] = csrfToken;
    }
    return headers;
}
// ────────────────────────────────────────────────────────────────────────────

function escapeHtml(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

function nowTime() {
    return new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function shortText(value, max = 72) {
    const text = String(value ?? '').replace(/\s+/g, ' ').trim();
    return text.length > max ? `${text.slice(0, max - 3)}...` : text;
}

function setBadge(el, text, tone = 'neutral') {
    if (!el) return;
    el.textContent = text;
    el.className = `badge ${tone}`;
}

function safeJsonParse(value, fallback = null) {
    if (value == null) return fallback;
    try {
        const parsed = JSON.parse(value);
        return parsed ?? fallback;
    } catch (_) {
        return fallback;
    }
}

function createSessionUuid() {
    if (typeof crypto.randomUUID === 'function') {
        return crypto.randomUUID();
    }

    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function setCurrentSessionId(sessionId) {
    currentSessionId = sessionId || null;
    if (currentSessionId) {
        localStorage.setItem(CURRENT_SESSION_KEY, currentSessionId);
    } else {
        localStorage.removeItem(CURRENT_SESSION_KEY);
    }
}

function ensureCurrentSessionId() {
    if (!currentSessionId) {
        setCurrentSessionId(`session-${createSessionUuid()}`);
    }
    return currentSessionId;
}

/* ── OIDC config persistence ─────────────────────────────────────────── */

function getOidcConfig() {
    return safeJsonParse(localStorage.getItem(OIDC_CONFIG_KEY), { authority: '', clientId: '' });
}

function saveOidcConfig(cfg) {
    localStorage.setItem(OIDC_CONFIG_KEY, JSON.stringify(cfg));
}

function clearOidcConfig() {
    localStorage.removeItem(OIDC_CONFIG_KEY);
    sessionStorage.removeItem(OIDC_TOKEN_KEY);
    sessionStorage.removeItem(OIDC_STATE_KEY);
    sessionStorage.removeItem(OIDC_VERIFIER_KEY);
}

const OIDC_DEFAULT_AUTHORITY = 'https://passport.ai4c.cn/realms/ai4c-saas';
const OIDC_DEFAULT_CLIENT_ID  = 'ncrew-client';

function getClientAuthMode() {
    const v = localStorage.getItem(AUTH_CLIENT_MODE_KEY);
    return v === 'token' ? 'token' : 'oidc';
}

function setClientAuthMode(mode) {
    localStorage.setItem(AUTH_CLIENT_MODE_KEY, mode);
}

function applyAuthMode(mode) {
    const tokenSection = document.getElementById('auth-token-section');
    const oidcSection  = document.getElementById('auth-oidc-section');
    const radioToken   = document.getElementById('auth-mode-token');
    const radioOidc    = document.getElementById('auth-mode-oidc');
    if (tokenSection) tokenSection.hidden = (mode === 'oidc');
    if (oidcSection)  oidcSection.hidden  = (mode !== 'oidc');
    if (radioToken)   radioToken.checked  = (mode !== 'oidc');
    if (radioOidc)    radioOidc.checked   = (mode === 'oidc');
}

function base64urlEncode(buffer) {
    const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    let s = ''; for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function generatePkce() {
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    return { verifier, challenge: base64urlEncode(hash) };
}

async function fetchOidcDiscovery(authority) {
    const resp = await fetch(authority.replace(/\/+$/, '') + '/.well-known/openid-configuration');
    if (!resp.ok) throw new Error('OIDC discovery failed: ' + resp.status);
    return resp.json();
}

async function initiateOidcLogin() {
    const cfg = getOidcConfig();
    if (!cfg.authority) { appendSystem('先在 Connection 设置里填写 OIDC Authority URL。', true); return; }
    if (!cfg.clientId)  { appendSystem('先在 Connection 设置里填写 OIDC Client ID。', true); return; }
    try {
        const { verifier, challenge } = await generatePkce();
        const state = base64urlEncode(crypto.getRandomValues(new Uint8Array(16)));
        sessionStorage.setItem(OIDC_VERIFIER_KEY, verifier);
        sessionStorage.setItem(OIDC_STATE_KEY, state);
        let authEndpoint;
        try { authEndpoint = (await fetchOidcDiscovery(cfg.authority)).authorization_endpoint; }
        catch (_) { authEndpoint = cfg.authority.replace(/\/+$/, '') + '/protocol/openid-connect/auth'; }
        const redirectUri = window.location.origin + window.location.pathname;
        const params = new URLSearchParams({
            response_type: 'code', client_id: cfg.clientId,
            redirect_uri: redirectUri, scope: 'openid profile email',
            code_challenge: challenge, code_challenge_method: 'S256', state
        });
        window.location.assign(authEndpoint + '?' + params);
    } catch (err) { appendSystem('OIDC 登录失败: ' + err.message, true); }
}

async function handleOidcCallback() {
    const params = new URLSearchParams(window.location.search);
    const code = params.get('code'), returnedState = params.get('state');
    if (!code || !returnedState) return;
    const storedState = sessionStorage.getItem(OIDC_STATE_KEY);
    const verifier   = sessionStorage.getItem(OIDC_VERIFIER_KEY);
    window.history.replaceState({}, document.title, window.location.pathname);
    if (returnedState !== storedState || !verifier) {
        appendSystem('OIDC 回调被拒绝：state 不匹配。', true); return;
    }
    sessionStorage.removeItem(OIDC_STATE_KEY);
    sessionStorage.removeItem(OIDC_VERIFIER_KEY);
    const cfg = getOidcConfig();
    appendSystem('正在完成 OIDC 登录…');
    try {
        let tokenEndpoint;
        try { tokenEndpoint = (await fetchOidcDiscovery(cfg.authority)).token_endpoint; }
        catch (_) { tokenEndpoint = cfg.authority.replace(/\/+$/, '') + '/protocol/openid-connect/token'; }
        const redirectUri = window.location.origin + window.location.pathname;
        const resp = await fetch(tokenEndpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({
                grant_type: 'authorization_code', client_id: cfg.clientId,
                redirect_uri: redirectUri, code, code_verifier: verifier
            })
        });
        if (!resp.ok) { const e = await resp.json().catch(() => ({})); throw new Error(e.error_description || 'HTTP ' + resp.status); }
        const tokens = await resp.json();
        if (!tokens.access_token) throw new Error('No access_token in response');
        sessionStorage.setItem(OIDC_TOKEN_KEY, tokens.access_token);
        setClientAuthMode('oidc'); // persist the mode switch after a successful login
        appendSystem('OIDC 登录成功，正在重新连接…');
        if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
        reconnectAttempts = 0;
        if (ws && ws.readyState !== WebSocket.CLOSED) { try { ws.close(); } catch (_) {} }
        connect();
    } catch (err) { appendSystem('OIDC token 交换失败: ' + err.message, true); }
}

/* ---------- Theme ---------- */
function getPreferredTheme() {
    const stored = localStorage.getItem(THEME_KEY);
    if (stored === 'light' || stored === 'dark') return stored;
    return 'light';
}

function applyTheme(theme) {
    const value = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.setAttribute('data-theme', value);
    if (themeIconLight && themeIconDark) {
        themeIconLight.hidden = value === 'dark';
        themeIconDark.hidden = value === 'light';
    }
    if (themeToggleGroup) {
        themeToggleGroup.forEach((btn) => {
            btn.setAttribute('aria-pressed', btn.dataset.themeValue === value ? 'true' : 'false');
        });
    }
}

function toggleTheme() {
    const next = (document.documentElement.getAttribute('data-theme') === 'dark') ? 'light' : 'dark';
    localStorage.setItem(THEME_KEY, next);
    applyTheme(next);
}

function setTheme(value) {
    localStorage.setItem(THEME_KEY, value);
    applyTheme(value);
}

/* ---------- Provider setup (frontend-only; TODO: wire to backend later) ----------
   Security note: the API key is intentionally NOT persisted to
   localStorage. Anything in localStorage is readable by any
   JavaScript running on this origin (e.g. via an XSS bug), so we
   split storage:
     - non-secret metadata (provider, baseUrl, model) → localStorage
     - the API key → sessionStorage (cleared when the tab closes)
   Future backend wiring should store the key server-side instead. */
const PROVIDER_KEY_SESSION = 'openclaw_provider_key_session';

function loadProviderSetup() {
    const meta = safeJsonParse(localStorage.getItem(PROVIDER_SETUP_KEY), null);
    if (!meta) return null;
    const key = sessionStorage.getItem(PROVIDER_KEY_SESSION) || null;
    return { ...meta, key };
}

function saveProviderSetup(data) {
    // TODO: wire to backend provider endpoint once available.
    // We never log the key; metadata persists locally, key is session-only.
    const { key, ...meta } = data || {};
    try {
        localStorage.setItem(PROVIDER_SETUP_KEY, JSON.stringify(meta));
        if (key) {
            sessionStorage.setItem(PROVIDER_KEY_SESSION, key);
        } else {
            sessionStorage.removeItem(PROVIDER_KEY_SESSION);
        }
    } catch (_) { /* ignore quota errors */ }
}

function maskedKey(key) {
    if (!key) return '';
    const trimmed = String(key);
    if (trimmed.length <= 4) return '••••';
    return `••••${trimmed.slice(-4)}`;
}

function describeProviderSetup() {
    const setup = loadProviderSetup();
    if (!setup || setup.provider === 'runtime' || !setup.provider) {
        return { label: 'Local / runtime default', summary: 'None' };
    }
    const providerLabels = {
        'openai-compatible': 'OpenAI-compatible',
        'anthropic': 'Anthropic',
        'gemini': 'Gemini'
    };
    const label = providerLabels[setup.provider] || setup.provider;
    const parts = [];
    if (setup.model) parts.push(setup.model);
    if (setup.key) parts.push(maskedKey(setup.key));
    return { label: `${label} (local)`, summary: parts.join(' · ') || 'configured' };
}

function renderProviderStatus() {
    const { label, summary } = describeProviderSetup();
    if (providerStatus) providerStatus.textContent = label;
    if (providerConfigSummary) providerConfigSummary.textContent = summary;
    if (firstRunProvider) firstRunProvider.textContent = label;
}

/* ---------- Settings drawer ----------
   Close uses a transition-end fallback timer; opening cancels any
   pending close so a quick re-open can't be undone by the prior
   hide. Focus is restored only after the drawer has actually been
   hidden, so keyboard focus never lands behind a still-visible
   modal during the closing animation. */
let settingsCloseTimer = null;
let pendingSettingsFocus = null;

function openSettingsDrawer() {
    if (!settingsDrawer) return;
    // Cancel a pending close so we never hide an open drawer.
    if (settingsCloseTimer) {
        clearTimeout(settingsCloseTimer);
        settingsCloseTimer = null;
    }
    pendingSettingsFocus = null;
    lastFocusedBeforeModal = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    settingsDrawer.hidden = false;
    settingsBackdrop.hidden = false;
    // allow layout flush so transition runs
    requestAnimationFrame(() => {
        settingsDrawer.setAttribute('data-open', 'true');
        settingsBackdrop.setAttribute('data-open', 'true');
    });
    settingsButton?.setAttribute('aria-expanded', 'true');
    const first = focusableElements(settingsDrawer)[0];
    first?.focus();
}

function closeSettingsDrawer() {
    if (!settingsDrawer || settingsDrawer.hidden) return;
    settingsDrawer.removeAttribute('data-open');
    settingsBackdrop.removeAttribute('data-open');
    settingsButton?.setAttribute('aria-expanded', 'false');
    pendingSettingsFocus = lastFocusedBeforeModal || settingsButton;
    lastFocusedBeforeModal = null;
    if (settingsCloseTimer) clearTimeout(settingsCloseTimer);
    settingsCloseTimer = setTimeout(() => {
        settingsCloseTimer = null;
        // Re-check: if a reopen happened during the animation,
        // the drawer is now data-open=true and we must not hide it.
        if (settingsDrawer.getAttribute('data-open') === 'true') {
            pendingSettingsFocus = null;
            return;
        }
        settingsDrawer.hidden = true;
        settingsBackdrop.hidden = true;
        const target = pendingSettingsFocus;
        pendingSettingsFocus = null;
        target?.focus();
    }, 220);
}

/* ---------- Provider modal ---------- */
function openProviderModal() {
    if (!providerModal) return;
    const setup = loadProviderSetup() || {};
    providerSelect.value = setup.provider || 'runtime';
    providerKeyInput.value = setup.key || '';
    providerBaseUrlInput.value = setup.baseUrl || '';
    providerModelInput.value = setup.model || '';
    providerBaseUrlField.hidden = providerSelect.value !== 'openai-compatible';
    openModal(providerModal, providerSelect);
}

function closeProviderModal() {
    if (!providerModal) return;
    closeModal(providerModal, settingsButton);
    showNextToolApproval();
}

/* ---------- First-run card ---------- */
function isFirstRunDismissed() {
    return localStorage.getItem(FIRST_RUN_DISMISSED_KEY) === '1';
}

function dismissFirstRunCard() {
    localStorage.setItem(FIRST_RUN_DISMISSED_KEY, '1');
    updateEmptyState();
    messageInput?.focus();
}

function resetFirstRunCard() {
    localStorage.removeItem(FIRST_RUN_DISMISSED_KEY);
    updateEmptyState();
}

/* ---------- Connection / auth banners ----------
   These two banners are mutually exclusive: when auth is required
   (token needed), we show the auth banner and suppress the
   connection banner entirely so the user is not shown two
   overlapping prompts. */
function updateConnectionBanner() {
    if (!connectionBanner || !authBanner) return;
    const tokenRequired = latestChatState?.authMode === 'unauthorized';
    const banner = connectionBanner;
    const action = connectionBannerAction;
    const text = connectionBannerText;

    if (tokenRequired) {
        authBanner.hidden = false;
        banner.hidden = true;
        if (action) action.hidden = true;
        return;
    }

    authBanner.hidden = true;

    if (connectionState === 'reconnecting') {
        banner.hidden = false;
        banner.className = 'banner banner--warning';
        text.textContent = 'Connection dropped. Reconnecting…';
        if (action) action.hidden = true;
    } else if (connectionState === 'disconnected') {
        banner.hidden = false;
        banner.className = 'banner banner--error';
        text.textContent = 'Connection lost. Reconnect to continue.';
        if (action) action.hidden = false;
    } else {
        banner.hidden = true;
        if (action) action.hidden = true;
    }
}

/* ---------- Connection pill (app bar) ---------- */
function updateConnectionPill(state, label) {
    if (!connectionPill) return;
    connectionPill.setAttribute('data-state', state);
    if (connectionPillLabel) connectionPillLabel.textContent = label;
    if (firstRunConnection) firstRunConnection.textContent = label;
}

function setConnectionState(state) {
    connectionState = state;
    const labels = {
        connected: ['Connected', 'success'],
        connecting: ['Connecting', 'info'],
        reconnecting: ['Reconnecting', 'warning'],
        disconnected: ['Disconnected', 'error'],
        auth_required: ['Auth required', 'warning']
    };
    const [label, tone] = labels[state] || labels.disconnected;
    setBadge(statusConnection, label, tone);
    setBadge(headerRuntimeBadge, label, tone);
    updateConnectionPill(state, label);
    updateConnectionBanner();
    updateComposerAvailability();
}

function renderRuntimeStatus() {
    const data = latestChatState || {};
    const authMode = data.authMode || 'unknown';
    const presetId = data.effectiveToolPresetId || 'web';
    const surface = data.effectiveToolSurface || 'web';
    const remembered = rememberToken.checked || Boolean(localStorage.getItem(TOKEN_KEY_PERSIST));
    const capabilities = Array.isArray(data.capabilitySummary) ? data.capabilitySummary : [];
    const authLabel = formatAuthMode(authMode, data);
    const authTone = authMode === 'unauthorized' ? 'warning' : authMode === 'unknown' ? 'neutral' : 'info';

    setBadge(statusAuth, `Auth ${authLabel}`, authTone);
    setBadge(statusSurface, `${surface}/${presetId}`, 'neutral');
    authSummaryMode.textContent = authLabel;
    authSummarySurface.textContent = `${surface}/${presetId}`;
    authSummaryRemember.textContent = remembered ? 'on' : 'off';
    authSummaryCapabilities.textContent = capabilities.length ? `${capabilities.length} status item${capabilities.length === 1 ? '' : 's'}` : 'none reported';

    statusCapabilities.innerHTML = '';
    for (const item of capabilities.slice(0, 3)) {
        const chip = document.createElement('span');
        chip.className = 'badge info';
        chip.textContent = shortText(item, 38);
        statusCapabilities.appendChild(chip);
    }
}

function updateEmptyState() {
    if (!emptyState) return;
    const hasUserMessage = Boolean(chatContainer.querySelector('.message-row.user-row'));
    emptyState.hidden = hasUserMessage;
    if (firstRunCard) {
        firstRunCard.hidden = hasUserMessage || isFirstRunDismissed();
    }
}

function addMessageMeta(div, label) {
    if (!div || div.querySelector('.message-meta')) return;
    const meta = document.createElement('div');
    meta.className = 'message-meta';
    meta.textContent = `${label} / ${nowTime()}`;
    div.prepend(meta);
}

function addCodeCopyButtons(container) {
    if (!container) return;
    container.querySelectorAll('pre').forEach((pre) => {
        if (pre.querySelector('.code-copy-button')) return;
        const code = pre.querySelector('code');
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'code-copy-button';
        button.textContent = 'Copy';
        button.addEventListener('click', async () => {
            try {
                await navigator.clipboard.writeText(code?.innerText || pre.innerText || '');
                button.textContent = 'Copied';
                setTimeout(() => { button.textContent = 'Copy'; }, 1200);
            } catch (_) {
                button.textContent = 'Failed';
            }
        });
        pre.prepend(button);
    });
}

function focusableElements(container) {
    if (!container) return [];
    return Array.from(container.querySelectorAll('button, [href], input, select, textarea, summary, [tabindex]:not([tabindex="-1"])'))
        .filter((el) => !el.disabled && !el.hidden && el.getClientRects().length > 0);
}

function openModal(modal, preferredFocus) {
    lastFocusedBeforeModal = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    modal.hidden = false;
    const target = preferredFocus || focusableElements(modal)[0];
    target?.focus();
}

function closeModal(modal, fallbackFocus) {
    modal.hidden = true;
    const target = lastFocusedBeforeModal || fallbackFocus;
    lastFocusedBeforeModal = null;
    target?.focus();
}

function trapModalFocus(event, modal) {
    if (event.key !== 'Tab' || modal.hidden) return;
    const focusables = focusableElements(modal);
    if (!focusables.length) {
        event.preventDefault();
        return;
    }
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
    }
}

async function refreshChatState() {
    try {
        const token = getCurrentToken();
        const headers = {};
        if (token && token !== 'bypass') {
            headers['Authorization'] = `Bearer ${token}`;
        }

        const resp = await fetch('/auth/session', { method: 'GET', headers });
        if (!resp.ok) {
            renderChatState({
                authMode: resp.status === 401 ? 'unauthorized' : 'unknown',
                publicBind: true,
                effectiveToolSurface: 'web',
                effectiveToolPresetId: 'web',
                capabilitySummary: ['Provide a valid bootstrap or operator token to use protected chat surfaces.']
            });
            return;
        }

        renderChatState(await resp.json());
    } catch (_) {
        renderChatState({
            authMode: 'unknown',
            effectiveToolSurface: 'web',
            effectiveToolPresetId: 'web',
            capabilitySummary: ['Unable to load chat status right now.']
        });
    }
}

function renderChatState(data) {
    latestChatState = data || {};
    renderRuntimeStatus();
    renderProviderStatus();
    updateConnectionBanner();
    updateComposerAvailability();
    const pills = [];
    const authMode = data?.authMode || 'unknown';
    const presetId = data?.effectiveToolPresetId || 'web';
    const surface = data?.effectiveToolSurface || 'web';
    const remembered = rememberToken.checked || Boolean(localStorage.getItem(TOKEN_KEY_PERSIST));
    const capabilities = Array.isArray(data?.capabilitySummary) ? data.capabilitySummary : [];

    pills.push(`<span class="state-pill"><strong>Auth</strong>${escapeHtml(formatAuthMode(authMode, data))}</span>`);
    pills.push(`<span class="state-pill"><strong>Surface</strong>${escapeHtml(`${surface}/${presetId}`)}</span>`);
    pills.push(`<span class="state-pill"><strong>Remember</strong>${remembered ? 'on' : 'off'}</span>`);

    for (const item of capabilities.slice(0, 3)) {
        pills.push(`<span class="state-pill">${escapeHtml(item)}</span>`);
    }

    chatStateBar.innerHTML = pills.join('');

    if (rawRuntimeState) {
        try {
            rawRuntimeState.textContent = JSON.stringify(latestChatState, null, 2);
        } catch (_) {
            rawRuntimeState.textContent = '{}';
        }
    }
}

function formatAuthMode(authMode, data) {
    switch ((authMode || '').toLowerCase()) {
        case 'loopback-open':
            return 'loopback local';
        case 'browser-session':
            return 'browser session';
        case 'account_token':
            return 'operator token';
        case 'bearer':
            return data?.isBootstrapAdmin ? 'bootstrap token' : 'bearer';
        case 'oidc_jwt':
            return 'OIDC / JWT';
        case 'unauthorized':
            return data?.publicBind ? 'token required' : 'not signed in';
        default:
            return authMode || 'unknown';
    }
}

function sanitizeRenderedHtml(html) {
    return DOMPurify.sanitize(html, {
        ADD_TAGS: ['audio', 'source'],
        ADD_ATTR: ['controls', 'src', 'type', 'preload']
    });
}

function preprocessMediaMarkers(raw) {
    const lines = (raw || "").split('\n');
    const out = [];
    for (const line of lines) {
        const trimmed = line.trim();
        const mImgUrl = trimmed.match(/^\[IMAGE_URL:(.+)\]$/);
        if (mImgUrl) {
            const url = mImgUrl[1].trim();
            out.push(`![](${url})`);
            continue;
        }
        const mFileUrl = trimmed.match(/^\[FILE_URL:(.+)\]$/);
        if (mFileUrl) {
            const rawVal = mFileUrl[1].trim();
            // Strip the |filename suffix from the URL (it's metadata, not part of the path).
            const pipeIdx = rawVal.indexOf('|');
            const fileUrl = pipeIdx >= 0 ? rawVal.slice(0, pipeIdx) : rawVal;
            const fileName = pipeIdx >= 0 ? rawVal.slice(pipeIdx + 1) : (fileUrl.split('/').pop() || 'file');
            out.push(`[\uD83D\uDCCE ${fileName}](${fileUrl})`);
            continue;
        }
        const mAudioUrl = trimmed.match(/^\[AUDIO_URL:(.+)\]$/);
        if (mAudioUrl) {
            const url = mAudioUrl[1].trim();
            out.push(`<audio controls preload="metadata" src="${url}"></audio>`);
            continue;
        }
        out.push(line);
    }
    return out.join('\n');
}

function appendAssistantMarkdown(md) {
    const row = createRow('assistant');
    const div = document.createElement('div');
    div.className = 'message assistant';
    div.innerHTML = sanitizeRenderedHtml(marked.parse(preprocessMediaMarkers(md)));
    addMessageMeta(div, 'Assistant');
    div.querySelectorAll('pre code').forEach((block) => hljs.highlightElement(block));
    addCodeCopyButtons(div);
    row.appendChild(div);
    chatContainer.insertBefore(row, typingRow);
    updateEmptyState();
    scrollToBottom();
}

function scrollToBottom(smooth = true) {
    chatWrapper.scrollTo({
        top: chatWrapper.scrollHeight,
        behavior: smooth ? 'smooth' : 'auto'
    });
}

function renderActiveResponse(finalRender = false) {
    if (!activeResponseDiv || !activeRawContent) {
        return;
    }

    const meta = activeResponseDiv.querySelector('.message-meta')?.outerHTML || '';
    activeResponseDiv.innerHTML = meta + sanitizeRenderedHtml(marked.parse(preprocessMediaMarkers(activeRawContent)));

    if (finalRender) {
        activeResponseDiv.querySelectorAll('pre code').forEach((block) => {
            hljs.highlightElement(block);
        });
        addCodeCopyButtons(activeResponseDiv);
        scrollToBottom();
        return;
    }

    scrollToBottom(false);
}

function scheduleActiveResponseRender(finalRender = false) {
    if (finalRender) {
        if (streamRenderTimer) {
            clearTimeout(streamRenderTimer);
            streamRenderTimer = null;
        }
        renderActiveResponse(true);
        return;
    }

    if (streamRenderTimer) {
        return;
    }

    streamRenderTimer = setTimeout(() => {
        streamRenderTimer = null;
        renderActiveResponse(false);
    }, WEBCHAT_CONFIG.streamRenderDebounceMs);
}

function resetActiveResponse() {
    typingRow.style.display = 'none';
    setStopMode(false);
    if (streamRenderTimer) {
        clearTimeout(streamRenderTimer);
        streamRenderTimer = null;
    }
    if (activeResponseDiv && activeRawContent) {
        renderActiveResponse(true);
    }
    activeResponseDiv = null;
    activeRawContent = '';
}

function createRow(type) {
    const row = document.createElement('div');
    row.className = `message-row ${type}-row`;
    
    if (type === 'assistant') {
        const avatar = document.createElement('div');
        avatar.className = 'agent-avatar';
        avatar.innerHTML = '<img src="image.png" alt="Agent" />';
        row.appendChild(avatar);
    } else if (type === 'user') {
        const avatar = document.createElement('div');
        avatar.className = 'user-avatar';
        avatar.innerHTML = 'U';
        row.appendChild(avatar);
    }

    return row;
}

function appendSystem(text, isError = false) {
    const row = createRow('system');
    const div = document.createElement('div');

    if (isError) {
        div.className = 'message system error';
        const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        icon.setAttribute('width', '16');
        icon.setAttribute('height', '16');
        icon.setAttribute('viewBox', '0 0 24 24');
        icon.setAttribute('fill', 'none');
        icon.setAttribute('stroke', 'currentColor');
        icon.setAttribute('stroke-width', '2');

        const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        circle.setAttribute('cx', '12');
        circle.setAttribute('cy', '12');
        circle.setAttribute('r', '10');

        const line1 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
        line1.setAttribute('x1', '12');
        line1.setAttribute('y1', '8');
        line1.setAttribute('x2', '12');
        line1.setAttribute('y2', '12');

        const line2 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
        line2.setAttribute('x1', '12');
        line2.setAttribute('y1', '16');
        line2.setAttribute('x2', '12.01');
        line2.setAttribute('y2', '16');

        icon.appendChild(circle);
        icon.appendChild(line1);
        icon.appendChild(line2);

        const textNode = document.createTextNode(` ${text}`);
        div.appendChild(icon);
        div.appendChild(textNode);
    } else {
        div.className = 'message system';
        div.textContent = text;
    }
    addMessageMeta(div, 'System');

    row.appendChild(div);

    chatContainer.insertBefore(row, typingRow);
    updateEmptyState();
    scrollToBottom();
}

function appendToolFailure(text) {
    const row = createRow('system');
    const div = document.createElement('div');
    div.className = 'tool-failure';
    const title = document.createElement('strong');
    title.textContent = `Tool failure - ${nowTime()}`;
    const body = document.createElement('div');
    body.textContent = text;
    div.appendChild(title);
    div.appendChild(body);
    row.appendChild(div);
    chatContainer.insertBefore(row, typingRow);
    updateEmptyState();
    scrollToBottom();
}

function appendToolPill(toolName) {
    const row = document.createElement('div');
    row.className = 'message-row system-row';
    const pill = document.createElement('div');
    pill.className = 'tool-pill';
    pill.textContent = `Agent invoked tool: ${toolName}`;

    row.appendChild(pill);
    chatContainer.insertBefore(row, typingRow);
    updateEmptyState();
    scrollToBottom();
}

function isToolFailureEnvelope(env) {
    const resultStatus = (env?.resultStatus || '').trim().toLowerCase();
    if (resultStatus && resultStatus !== 'completed') {
        return true;
    }

    const normalized = ((env?.failureMessage || env?.text || env?.content) || '').trim().toLowerCase();
    if (!normalized) {
        return false;
    }

    return normalized.startsWith('error:')
        || normalized.startsWith('browser action failed:')
        || normalized.includes('requires approval')
        || normalized.includes('denied')
        || normalized.includes('timed out')
        || normalized.includes('failed');
}

function explainToolFailure(env) {
    const code = (env?.failureCode || '').trim().toLowerCase();
    const nextStep = (env?.nextStep || '').trim();
    const toolName = (env?.toolName || 'tool').trim();
    const raw = ((env?.failureMessage || env?.text || env?.content) || '').trim();

    if (code === 'preset_blocked') {
        return `${toolName} is blocked by the active preset on this surface.${nextStep ? ` ${nextStep}` : ''}`.trim();
    }

    if (code === 'approval_required') {
        return `This tool requires operator approval before it can run.${nextStep ? ` ${nextStep}` : ''}`.trim();
    }

    if (code === 'operator_auth_required') {
        return `This tool requires operator authentication on the current surface.${nextStep ? ` ${nextStep}` : ''}`.trim();
    }

    if (code === 'browser_backend_missing' || code === 'runtime_capability_unavailable') {
        return `This tool is unavailable in the current runtime.${nextStep ? ` ${nextStep}` : ''}`.trim();
    }

    if (code === 'timeout') {
        return `This tool timed out before it completed.${nextStep ? ` ${nextStep}` : ''}`.trim();
    }

    const presetMatch = raw.match(/Tool '([^']+)' is not allowed for preset '([^']+)'/i);
    if (presetMatch) {
        return `Tool '${presetMatch[1]}' is blocked by preset '${presetMatch[2]}' on this chat surface. Use a broader preset on an HTTP surface, or change the web surface binding if that access is intentional.`;
    }

    const normalized = raw.toLowerCase();
    if (normalized.includes('requires approval')) {
        return 'This tool requires operator approval before it can run.';
    }

    if (normalized.includes('operator auth') || normalized.includes('forbidden') || normalized.includes('unauthorized')) {
        return 'This tool requires operator authentication on the current surface.';
    }

    if (normalized.includes('browser') && (normalized.includes('unavailable') || normalized.includes('backend'))) {
        return 'Browser is unavailable in this runtime. Configure a browser backend or sandbox, or disable the browser tool.';
    }

    return raw || 'Tool execution failed.';
}

function isLiveMode() {
    return modeSelect.value === 'live';
}

function isLiveConnected() {
    return liveWs && liveWs.readyState === WebSocket.OPEN;
}

function isLiveConnecting() {
    return liveWs && liveWs.readyState === WebSocket.CONNECTING;
}

function updateComposerAvailability() {
    const chatReady = ws && ws.readyState === WebSocket.OPEN;
    const liveReady = isLiveConnected();
    const liveConnecting = isLiveConnecting();
    const canSend = isLiveMode() ? liveReady : chatReady;
    const tokenRequired = latestChatState?.authMode === 'unauthorized';
    messageInput.disabled = !canSend;
    sendButton.disabled = !canSend;
    liveMicButton.disabled = !liveReady;
    liveInterruptButton.disabled = !liveReady;
    liveInterruptButton.hidden = !liveReady;
    liveConnectButton.disabled = liveConnecting;
    liveConnectButton.textContent = liveReady ? 'Stop Live' : liveConnecting ? 'Starting Live...' : 'Start Live';
    liveStatusBadge.textContent = liveReady ? `Live online (${liveProviderSelect.value})` : liveConnecting ? 'Live connecting' : 'Live offline';
    liveStatusBadge.classList.toggle('online', liveReady);
    liveMicStatus.textContent = liveMediaStream ? 'Mic streaming' : 'Mic muted';
    liveMicStatus.className = `live-bar__status ${liveMediaStream ? 'online' : ''}`;
    const shouldShowLive = isLiveMode() || Boolean(liveWs) || Boolean(liveMediaStream);
    liveToolbar.classList.toggle('active', shouldShowLive);
    liveToolbar.hidden = !shouldShowLive;
    setBadge(statusLive, liveReady ? `Live ${liveProviderSelect.value}` : liveConnecting ? 'Live connecting' : 'Live offline', liveReady ? 'success' : liveConnecting ? 'info' : 'neutral');
    statusLive.hidden = !isLiveMode() && !liveWs && !liveMediaStream;
    if (canSend) {
        composerDisabledReason.textContent = '';
    } else if (isLiveMode()) {
        composerDisabledReason.textContent = liveConnecting ? 'Connecting…' : 'Start Live first';
    } else if (tokenRequired) {
        composerDisabledReason.textContent = 'Token required — open Settings';
    } else if (connectionState === 'reconnecting' || connectionState === 'connecting') {
        composerDisabledReason.textContent = 'Connecting…';
    } else {
        composerDisabledReason.textContent = 'Disconnected';
    }
}

function buildLiveWsUrl() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const token = getCurrentToken();
    const query = token ? `?token=${encodeURIComponent(token)}` : '';
    return `${protocol}//${window.location.host}${getBasePath()}/ws/live${query}`;
}

function createAudioBlobFromBase64(base64Data, mimeType) {
    const binary = atob(base64Data);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }

    if ((mimeType || '').includes('audio/pcm')) {
        const rateMatch = /rate=(\d+)/i.exec(mimeType || '');
        const sampleRate = rateMatch ? Number(rateMatch[1]) : 24000;
        return createWavBlob(bytes, sampleRate);
    }

    return new Blob([bytes], { type: mimeType || 'audio/mpeg' });
}

function createWavBlob(pcmBytes, sampleRate) {
    const header = new ArrayBuffer(44);
    const view = new DataView(header);
    const byteRate = sampleRate * 2;
    const blockAlign = 2;
    writeAscii(view, 0, 'RIFF');
    view.setUint32(4, 36 + pcmBytes.length, true);
    writeAscii(view, 8, 'WAVE');
    writeAscii(view, 12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, byteRate, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, 16, true);
    writeAscii(view, 36, 'data');
    view.setUint32(40, pcmBytes.length, true);
    return new Blob([header, pcmBytes], { type: 'audio/wav' });
}

function writeAscii(view, offset, text) {
    for (let i = 0; i < text.length; i++) {
        view.setUint8(offset + i, text.charCodeAt(i));
    }
}

function enqueueLiveAudio(base64Data, mimeType) {
    const blob = createAudioBlobFromBase64(base64Data, mimeType);
    const url = URL.createObjectURL(blob);
    liveAudioQueue.push(url);
    if (!liveAudioPlaying) {
        playNextLiveAudio();
    }
}

function appendLiveTimeline(label, text = '') {
    if (!liveTimeline) return;
    const item = document.createElement('div');
    item.className = 'live-timeline-entry';
    item.innerHTML = `<strong>${escapeHtml(label)}</strong> ${escapeHtml(shortText(text, 120))}`;
    liveTimeline.appendChild(item);
    liveTimeline.scrollTop = liveTimeline.scrollHeight;
}

function playNextLiveAudio() {
    if (liveAudioQueue.length === 0) {
        liveAudioPlaying = false;
        return;
    }

    liveAudioPlaying = true;
    const nextUrl = liveAudioQueue.shift();
    const audio = new Audio(nextUrl);
    audio.onended = () => {
        URL.revokeObjectURL(nextUrl);
        playNextLiveAudio();
    };
    audio.onerror = () => {
        URL.revokeObjectURL(nextUrl);
        playNextLiveAudio();
    };
    audio.play().catch(() => {
        URL.revokeObjectURL(nextUrl);
        playNextLiveAudio();
    });
}

async function speakText(text) {
    const provider = speechProviderSelect.value;
    if (provider === 'live-native' || !text.trim()) {
        return;
    }

    const token = getCurrentToken();
    const headers = { 'Content-Type': 'application/json' };
    if (token && token !== 'bypass') {
        headers['Authorization'] = `Bearer ${token}`;
    }

    const resp = await fetch('/api/integration/text-to-speech', {
        method: 'POST',
        headers,
        body: JSON.stringify({
            text,
            provider,
            voiceId: voiceIdInput.value.trim() || null,
            voiceName: voiceIdInput.value.trim() || null
        })
    });

    if (!resp.ok) {
        const payload = await resp.json().catch(() => null);
        throw new Error(payload?.error || `Speech synthesis failed (${resp.status}).`);
    }

    const result = await resp.json();
    enqueueLiveAudio((result.dataUrl || '').split(',')[1] || '', result.mediaType || 'audio/mpeg');
}

async function connectLive() {
    if (isLiveConnected()) {
        disconnectLive();
        return;
    }
    if (isLiveConnecting()) {
        return;
    }
    if (window.location.protocol === 'file:' || !window.location.host) {
        appendSystem('Live mode requires opening webchat through the Gateway HTTP/S origin, not file://.', true);
        appendLiveTimeline('Error', 'Open through the Gateway URL');
        updateComposerAvailability();
        return;
    }

    persistToken(tokenInput.value.trim());
    const responseModalities = speechProviderSelect.value === 'live-native' ? ['TEXT', 'AUDIO'] : ['TEXT'];
    liveWs = new WebSocket(buildLiveWsUrl());
    updateComposerAvailability();

    liveWs.onopen = () => {
        liveWs.send(JSON.stringify({
            provider: liveProviderSelect.value,
            responseModalities,
            voiceName: voiceIdInput.value.trim() || null
        }));
        appendSystem(`Live session connecting via ${liveProviderSelect.value}...`);
        appendLiveTimeline('Connecting', liveProviderSelect.value);
        updateComposerAvailability();
        messageInput.focus();
    };

    liveWs.onmessage = async (event) => {
        const env = safeJsonParse(event.data, { type: 'text', text: event.data });
        switch (env.type) {
            case 'opened':
                appendSystem(`Live session opened: ${env.text || liveProviderSelect.value}`);
                appendLiveTimeline('Opened', env.text || liveProviderSelect.value);
                break;
            case 'text':
                typingRow.style.display = 'flex';
                if (!activeResponseDiv) {
                    const row = createRow('assistant');
                    activeResponseDiv = document.createElement('div');
                    activeResponseDiv.className = 'message assistant';
                    addMessageMeta(activeResponseDiv, 'Assistant');
                    row.appendChild(activeResponseDiv);
                    chatContainer.insertBefore(row, typingRow);
                }
                activeRawContent += env.text || '';
                scheduleActiveResponseRender(false);
                break;
            case 'audio':
                liveResponseHasAudio = true;
                if (speechProviderSelect.value === 'live-native' && env.base64Data) {
                    try {
                        enqueueLiveAudio(env.base64Data, env.mimeType || 'audio/wav');
                    } catch (_) {
                        appendLiveTimeline('Error', 'Audio decode failed');
                        appendSystem('Live audio decode failed.', true);
                    }
                }
                break;
            case 'input_transcription':
                appendSystem(`You said: ${env.text || ''}`);
                appendLiveTimeline('You', env.text || '');
                break;
            case 'output_transcription':
                appendSystem(`Live transcript: ${env.text || ''}`);
                appendLiveTimeline('Assistant', env.text || '');
                break;
            case 'turn_complete': {
                typingRow.style.display = 'none';
                scheduleActiveResponseRender(true);
                const completedText = activeRawContent;
                activeResponseDiv = null;
                activeRawContent = '';
                if (!liveResponseHasAudio && speechProviderSelect.value !== 'live-native') {
                    try {
                        await speakText(completedText);
                    } catch (error) {
                        appendSystem(error.message || 'Speech synthesis failed.', true);
                    }
                }
                liveResponseHasAudio = false;
                break;
            }
            case 'interrupted':
                appendSystem('Live generation interrupted.');
                appendLiveTimeline('Interrupted');
                break;
            case 'error':
                appendSystem(env.error || env.text || 'Live session error.', true);
                appendLiveTimeline('Error', env.error || env.text || '');
                break;
        }
        scrollToBottom();
    };

    liveWs.onclose = () => {
        stopLiveMicCapture(false);
        liveWs = null;
        resetActiveResponse();
        appendLiveTimeline('Closed');
        updateComposerAvailability();
    };

    liveWs.onerror = () => appendSystem('Live websocket encountered an error.', true);
}

function disconnectLive() {
    stopLiveMicCapture(false);
    if (liveWs) {
        try {
            liveWs.send(JSON.stringify({ type: 'close' }));
        } catch (_) { }
        liveWs.close();
        liveWs = null;
    }
    updateComposerAvailability();
}

async function startLiveMicCapture() {
    if (!isLiveConnected()) {
        appendSystem('Connect a live session before enabling the microphone.', true);
        return;
    }

    liveMediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    liveAudioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
    const liveInputSampleRate = Math.round(liveAudioContext.sampleRate || 16000);
    liveAudioSource = liveAudioContext.createMediaStreamSource(liveMediaStream);
    liveAudioProcessor = liveAudioContext.createScriptProcessor(4096, 1, 1);
    liveAudioProcessor.onaudioprocess = (event) => {
        if (!isLiveConnected()) return;
        const samples = event.inputBuffer.getChannelData(0);
        const pcm = new Int16Array(samples.length);
        for (let i = 0; i < samples.length; i++) {
            const value = Math.max(-1, Math.min(1, samples[i]));
            pcm[i] = value < 0 ? value * 0x8000 : value * 0x7fff;
        }
        const bytes = new Uint8Array(pcm.buffer);
        let binary = '';
        for (let i = 0; i < bytes.length; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        liveWs.send(JSON.stringify({
            type: 'audio',
            base64Data: btoa(binary),
            mimeType: `audio/pcm;rate=${liveInputSampleRate}`,
            turnComplete: false
        }));
    };
    liveAudioSource.connect(liveAudioProcessor);
    liveAudioProcessor.connect(liveAudioContext.destination);
    liveMicButton.classList.add('active');
    liveMicButton.textContent = 'Stop Mic';
    updateComposerAvailability();
    appendLiveTimeline('Mic', 'streaming');
    appendSystem('Live microphone streaming started.');
}

function stopLiveMicCapture(sendAudioEnd = true) {
    if (sendAudioEnd && isLiveConnected()) {
        try {
            liveWs.send(JSON.stringify({ type: 'audio_end' }));
        } catch (_) { }
    }

    if (liveAudioProcessor) {
        liveAudioProcessor.disconnect();
        liveAudioProcessor.onaudioprocess = null;
        liveAudioProcessor = null;
    }
    if (liveAudioSource) {
        liveAudioSource.disconnect();
        liveAudioSource = null;
    }
    if (liveAudioContext) {
        liveAudioContext.close().catch(() => { });
        liveAudioContext = null;
    }
    if (liveMediaStream) {
        liveMediaStream.getTracks().forEach(track => track.stop());
        liveMediaStream = null;
    }

    liveMicButton.classList.remove('active');
    liveMicButton.textContent = 'Mic';
    updateComposerAvailability();
    appendLiveTimeline('Mic', 'muted');
}

function sendCanvasReady() {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({
        type: 'canvas_ready',
        capabilities: ['a2ui.v0_8', 'a2ui.v0_9', 'canvas.present', 'canvas.hide', 'canvas.local_html', 'snapshot.state'],
        supportedCatalogIds: ['urn:a2ui:catalog:openclaw_v0_8', 'urn:a2ui:catalog:agenui_catalog']
    }));
}

function isCanvasEnvelopeType(type) {
    return [
        'canvas_present',
        'canvas_hide',
        'canvas_navigate',
        'canvas_snapshot',
        'a2ui_push',
        'a2ui_reset',
        'a2ui_eval',
        'a2ui_create_surface',
        'a2ui_update_components',
        'a2ui_update_data_model',
        'a2ui_delete_surface',
        'a2ui_sync_ui_to_data'
    ].includes(type);
}

function sendCanvasAck(env, success = true, error = null) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({
        type: 'canvas_ack',
        requestId: env.requestId,
        sessionId: canvasSessionId,
        surfaceId: env.surfaceId || 'main',
        success,
        error
    }));
}

function showCanvas() {
    canvasPanel.hidden = false;
    canvasStatus.textContent = 'Visible';
    statusCanvas.hidden = false;
    workspaceEl?.classList.add('workspace--split');
    updateCanvasMetadata();
}

function hideCanvas() {
    canvasPanel.hidden = true;
    canvasStatus.textContent = 'Hidden';
    workspaceEl?.classList.remove('workspace--split');
    updateCanvasMetadata();
}

function updateCanvasMetadata() {
    const surface = activeCanvasSurfaceId ? canvasSurfaces.get(activeCanvasSurfaceId) : null;
    const components = surface?.components || [];
    const isHtml = !canvasHtmlFrame.hidden;
    canvasMetaSurface.textContent = isHtml ? 'local html' : surface?.surfaceId || 'none';
    canvasMetaCount.textContent = isHtml ? 'HTML' : String(components.length);
    canvasMetaProtocol.textContent = isHtml ? 'sandboxed srcdoc' : surface ? `${surface.protocolVersion || 'unknown'} / ${surface.catalogId || 'catalog unknown'}` : 'pending';
    const updatedAt = isHtml ? canvasHtmlUpdatedAt : surface?.updatedAt;
    canvasMetaUpdated.textContent = updatedAt ? new Date(updatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : 'never';
    const hasCanvasContent = isHtml || components.length > 0 || canvasSurfaces.size > 0;
    canvasEmpty.hidden = hasCanvasContent;
    a2uiSurfaces.hidden = isHtml || !hasCanvasContent;
    statusCanvas.hidden = !hasCanvasContent && canvasPanel.hidden;
    setBadge(statusCanvas, canvasPanel.hidden ? 'Canvas hidden' : hasCanvasContent ? `Canvas ${canvasMetaSurface.textContent}` : 'Canvas ready', hasCanvasContent ? 'info' : 'neutral');

    const diagnostics = surface?.diagnostics || [];
    if (diagnostics.length) {
        canvasDiagnostics.hidden = false;
        canvasDiagnostics.textContent = diagnostics.slice(-4).join('\n');
    } else {
        canvasDiagnostics.hidden = true;
        canvasDiagnostics.textContent = '';
    }
}

function getCanvasSurface(surfaceId = 'main') {
    const id = surfaceId || 'main';
    let surface = canvasSurfaces.get(id);
    if (!surface) {
        surface = {
            surfaceId: id,
            catalogId: 'urn:a2ui:catalog:openclaw_v0_8',
            protocolVersion: 'v0_8',
            title: id === 'main' ? 'Main' : id,
            components: [],
            dataModelJson: null,
            dataModel: null,
            values: {},
            diagnostics: [],
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            deleted: false
        };
        canvasSurfaces.set(id, surface);
    }
    return surface;
}

function setActiveCanvasSurface(surfaceId) {
    if (!canvasSurfaces.has(surfaceId)) return;
    activeCanvasSurfaceId = surfaceId;
    renderCanvasSurfaces();
}

function renderCanvasSurfaces() {
    a2uiTabs.innerHTML = '';
    for (const surface of canvasSurfaces.values()) {
        if (surface.deleted) continue;
        const tab = document.createElement('button');
        tab.type = 'button';
        tab.className = `a2ui-tab${surface.surfaceId === activeCanvasSurfaceId ? ' active' : ''}`;
        tab.textContent = surface.title || surface.surfaceId;
        tab.addEventListener('click', () => setActiveCanvasSurface(surface.surfaceId));
        a2uiTabs.appendChild(tab);
    }

    a2uiSurfaceHost.innerHTML = '';
    const surface = activeCanvasSurfaceId ? canvasSurfaces.get(activeCanvasSurfaceId) : null;
    if (!surface || surface.deleted) {
        updateCanvasMetadata();
        return;
    }
    for (const component of surface.components) {
        renderA2uiFrame(component, surface.surfaceId, surface);
    }
    updateCanvasMetadata();
}

function parseA2UiComponent(component) {
    return typeof component === 'string' ? JSON.parse(component) : component;
}

function resetA2ui() {
    canvasSurfaces.clear();
    activeCanvasSurfaceId = null;
    a2uiSurfaces.hidden = false;
    canvasHtmlFrame.hidden = true;
    canvasHtmlFrame.setAttribute('sandbox', '');
    canvasHtmlFrame.removeAttribute('srcdoc');
    canvasHtmlUpdatedAt = null;
    renderCanvasSurfaces();
    canvasStatus.textContent = 'Reset';
    updateCanvasMetadata();
}

function handleCanvasEnvelope(env) {
    canvasSessionId = env.sessionId || canvasSessionId;
    try {
        switch (env.type) {
            case 'canvas_present':
                showCanvas();
                sendCanvasAck(env);
                break;
            case 'canvas_hide':
                hideCanvas();
                sendCanvasAck(env);
                break;
            case 'canvas_navigate':
                handleCanvasNavigate(env);
                break;
            case 'a2ui_reset':
                resetA2ui();
                sendCanvasAck(env);
                break;
            case 'a2ui_push':
                handleA2uiPush(env);
                break;
            case 'a2ui_create_surface':
                handleA2uiCreateSurface(env);
                break;
            case 'a2ui_update_components':
                handleA2uiUpdateComponents(env);
                break;
            case 'a2ui_update_data_model':
                handleA2uiUpdateDataModel(env);
                break;
            case 'a2ui_delete_surface':
                handleA2uiDeleteSurface(env);
                break;
            case 'a2ui_sync_ui_to_data':
                handleA2uiSyncUiToData(env);
                break;
            case 'canvas_snapshot':
                sendCanvasSnapshot(env);
                break;
            case 'a2ui_eval':
                handleA2uiEval(env);
                break;
        }
    } catch (error) {
        const message = String(error?.message || error);
        getCanvasSurface(env.surfaceId || activeCanvasSurfaceId || 'main').diagnostics.push(message);
        updateCanvasMetadata();
        sendCanvasAck(env, false, message);
    }
}

function handleCanvasNavigate(env) {
    showCanvas();
    if (env.html) {
        canvasHtmlFrame.hidden = false;
        a2uiSurfaces.hidden = true;
        canvasHtmlFrame.setAttribute('sandbox', '');
        canvasHtmlFrame.srcdoc = env.html;
        canvasHtmlUpdatedAt = new Date().toISOString();
        canvasStatus.textContent = 'Local HTML';
        updateCanvasMetadata();
        sendCanvasAck(env);
        return;
    }
    if (!env.url || env.url === 'about:blank') {
        resetA2ui();
        showCanvas();
        sendCanvasAck(env);
        return;
    }
    if (String(env.url).startsWith('openclaw-canvas://')) {
        updateCanvasMetadata();
        sendCanvasAck(env, false, 'openclaw-canvas:// artifact loading is not implemented in webchat v1.');
        return;
    }
    updateCanvasMetadata();
    sendCanvasAck(env, false, 'Canvas v1 rejects remote webpage navigation; use the browser tool.');
}

function handleA2uiPush(env) {
    showCanvas();
    canvasHtmlFrame.hidden = true;
    canvasHtmlUpdatedAt = null;
    a2uiSurfaces.hidden = false;
    const surface = getCanvasSurface('main');
    surface.catalogId = surface.catalogId || 'urn:a2ui:catalog:openclaw_v0_8';
    surface.protocolVersion = 'v0_8';
    surface.deleted = false;
    const lines = String(env.frames || '').split(/\r?\n/).map(line => line.trim()).filter(Boolean);
    for (const line of lines) {
        surface.components.push(JSON.parse(line));
    }
    surface.updatedAt = new Date().toISOString();
    activeCanvasSurfaceId = surface.surfaceId;
    renderCanvasSurfaces();
    canvasStatus.textContent = `${surface.title || surface.surfaceId}: ${lines.length} frame${lines.length === 1 ? '' : 's'}`;
    sendCanvasAck(env);
}

function handleA2uiCreateSurface(env) {
    showCanvas();
    canvasHtmlFrame.hidden = true;
    canvasHtmlUpdatedAt = null;
    a2uiSurfaces.hidden = false;
    const surface = getCanvasSurface(env.surfaceId || 'main');
    surface.catalogId = env.catalogId || surface.catalogId || 'urn:a2ui:catalog:agenui_catalog';
    surface.protocolVersion = 'v0_9';
    surface.title = env.surfaceTitle || surface.title || surface.surfaceId;
    surface.components = Array.isArray(env.components) ? env.components.map(parseA2UiComponent) : [];
    surface.dataModelJson = env.dataModelJson || null;
    surface.dataModel = surface.dataModelJson ? JSON.parse(surface.dataModelJson) : null;
    surface.values = {};
    surface.diagnostics = [];
    surface.deleted = false;
    surface.updatedAt = new Date().toISOString();
    activeCanvasSurfaceId = surface.surfaceId;
    renderCanvasSurfaces();
    canvasStatus.textContent = `Surface ${surface.title || surface.surfaceId} created`;
    sendCanvasAck(env);
}

function handleA2uiUpdateComponents(env) {
    showCanvas();
    canvasHtmlFrame.hidden = true;
    canvasHtmlUpdatedAt = null;
    a2uiSurfaces.hidden = false;
    const surface = getCanvasSurface(env.surfaceId || 'main');
    if (env.catalogId) surface.catalogId = env.catalogId;
    surface.protocolVersion = 'v0_9';
    surface.components = Array.isArray(env.components) ? env.components.map(parseA2UiComponent) : [];
    surface.deleted = false;
    surface.updatedAt = new Date().toISOString();
    activeCanvasSurfaceId = surface.surfaceId;
    renderCanvasSurfaces();
    canvasStatus.textContent = `${surface.title || surface.surfaceId}: ${surface.components.length} component${surface.components.length === 1 ? '' : 's'}`;
    sendCanvasAck(env);
}

function handleA2uiUpdateDataModel(env) {
    const surface = getCanvasSurface(env.surfaceId || 'main');
    surface.dataModelJson = env.dataModelJson || '{}';
    surface.dataModel = JSON.parse(surface.dataModelJson);
    surface.updatedAt = new Date().toISOString();
    renderCanvasSurfaces();
    canvasStatus.textContent = `${surface.title || surface.surfaceId}: data updated`;
    sendCanvasAck(env);
}

function handleA2uiDeleteSurface(env) {
    const id = env.surfaceId || 'main';
    const surface = canvasSurfaces.get(id);
    if (surface) surface.deleted = true;
    canvasSurfaces.delete(id);
    if (activeCanvasSurfaceId === id) {
        activeCanvasSurfaceId = canvasSurfaces.keys().next().value || null;
    }
    renderCanvasSurfaces();
    canvasStatus.textContent = `Surface ${id} deleted`;
    sendCanvasAck(env);
}

function handleA2uiSyncUiToData(env) {
    const surface = getCanvasSurface(env.surfaceId || 'main');
    const dataModelJson = syncedDataModelJson(surface);
    ws.send(JSON.stringify({
        type: 'a2ui_sync_result',
        requestId: env.requestId,
        sessionId: canvasSessionId,
        surfaceId: surface.surfaceId,
        componentId: env.componentId || null,
        syncMode: env.syncMode || null,
        success: true,
        dataModelJson,
        valueJson: dataModelJson
    }));
    sendCanvasAck(env);
}

const A2UI_TYPE_ALIASES = {
    text: 'Text',
    markdown: 'Markdown',
    card: 'Card',
    button: 'Button',
    input: 'TextField',
    select: 'ChoicePicker',
    checklist: 'CheckBox',
    table: 'Table',
    image: 'Image',
    progress: 'Progress',
    chart: 'Chart'
};

const A2UI_RENDERERS = {
    Text: renderA2uiText,
    Markdown: renderA2uiMarkdown,
    RichText: renderA2uiMarkdown,
    Button: renderA2uiButton,
    TextField: renderA2uiInput,
    CheckBox: renderA2uiCheckBox,
    Progress: renderA2uiProgress,
    Slider: renderA2uiProgress,
    ChoicePicker: renderA2uiSelect,
    DateTimeInput: renderA2uiDateTimeInput,
    Row: renderA2uiContainer,
    Column: renderA2uiContainer,
    Card: renderA2uiCard,
    List: renderA2uiList,
    Tabs: renderA2uiTabs,
    Modal: renderA2uiCard,
    Table: renderA2uiTable,
    Image: renderA2uiImage,
    Divider: renderA2uiDivider,
    AudioPlayer: renderA2uiMediaPlaceholder,
    Video: renderA2uiMediaPlaceholder,
    Icon: renderA2uiIcon,
    Carousel: renderA2uiCarousel,
    Web: renderA2uiWebFallback,
    Chart: renderA2uiChart
};

function renderA2uiFrame(frame, surfaceId, surface = getCanvasSurface(surfaceId), parent = a2uiSurfaceHost) {
    const type = canonicalA2uiType(frame.type);
    const wrap = document.createElement('div');
    wrap.className = type === 'Card' ? 'a2ui-card' : 'a2ui-frame';
    wrap.dataset.frameId = frame.id || '';
    wrap.dataset.type = type;
    if (frame.visibleBinding && !asBoolean(readDataModelPath(surface, frame.visibleBinding))) {
        wrap.hidden = true;
    }
    const title = resolveBoundText(surface, frame.title || frame.label || '', frame.titleBinding || frame.labelBinding);
    if (title && type !== 'Button') {
        const titleEl = document.createElement('div');
        titleEl.className = 'a2ui-title';
        titleEl.textContent = title;
        wrap.appendChild(titleEl);
    }
    const renderer = A2UI_RENDERERS[type] || renderA2uiFallback;
    renderer(wrap, frame, surfaceId, surface, type);
    parent.appendChild(wrap);
}

function canonicalA2uiType(type) {
    const raw = String(type || 'Text');
    return A2UI_TYPE_ALIASES[raw] || A2UI_TYPE_ALIASES[raw.toLowerCase()] || raw;
}

function readDataModelPath(surface, path) {
    if (!path) return undefined;
    return String(path).split('.').filter(Boolean).reduce((value, segment) => value?.[segment], surface.dataModel);
}

function asBoolean(value) {
    return value === undefined || value === null ? true : Boolean(value);
}

function boundValue(surface, frame, fallback, bindingName = 'valueBinding') {
    const bound = readDataModelPath(surface, frame[bindingName]);
    return bound === undefined ? fallback : bound;
}

function setDataModelPath(surface, path, value) {
    if (!path) return;
    if (!surface.dataModel || typeof surface.dataModel !== 'object' || Array.isArray(surface.dataModel)) {
        surface.dataModel = {};
    }
    const segments = String(path).split('.').filter(Boolean);
    let current = surface.dataModel;
    for (let i = 0; i < segments.length - 1; i++) {
        const segment = segments[i];
        if (!current[segment] || typeof current[segment] !== 'object' || Array.isArray(current[segment])) {
            current[segment] = {};
        }
        current = current[segment];
    }
    if (segments.length) {
        current[segments[segments.length - 1]] = value;
        surface.dataModelJson = JSON.stringify(surface.dataModel);
    }
}

function setSurfaceValue(surface, frame, value) {
    if (frame.id) surface.values[frame.id] = value;
    setDataModelPath(surface, frame.valueBinding, value);
}

function syncedDataModelJson(surface) {
    if (surface.dataModel && typeof surface.dataModel === 'object' && !Array.isArray(surface.dataModel)) {
        return JSON.stringify(surface.dataModel);
    }
    return JSON.stringify(surface.values || {});
}

function resolveBoundText(surface, value, binding) {
    const bound = readDataModelPath(surface, binding);
    if (bound !== undefined) return String(bound ?? '');
    return String(value ?? '').replace(/\{([A-Za-z0-9_.-]+)\}/g, (_, path) => {
        const resolved = readDataModelPath(surface, path);
        return resolved === undefined || resolved === null ? '' : String(resolved);
    });
}

function appendMarkdownOrText(parent, value, markdown) {
    const div = document.createElement('div');
    if (markdown) {
        div.innerHTML = sanitizeRenderedHtml(marked.parse(String(value || '')));
    } else {
        div.textContent = String(value || '');
    }
    parent.appendChild(div);
}

function renderA2uiText(parent, frame, _surfaceId, surface) {
    appendMarkdownOrText(parent, resolveBoundText(surface, frame.text ?? frame.value ?? frame.label ?? '', frame.textBinding), false);
}

function renderA2uiMarkdown(parent, frame, _surfaceId, surface) {
    appendMarkdownOrText(parent, resolveBoundText(surface, frame.markdown ?? frame.text ?? frame.value ?? '', frame.textBinding), true);
}

function renderA2uiCard(parent, frame, surfaceId, surface) {
    appendMarkdownOrText(parent, resolveBoundText(surface, frame.body || frame.text || '', frame.textBinding), true);
    renderChildComponents(parent, frame, surfaceId, surface);
}

function renderA2uiButton(parent, frame, surfaceId, surface) {
    const button = document.createElement('button');
    button.className = 'a2ui-button';
    button.type = 'button';
    button.textContent = resolveBoundText(surface, frame.label || frame.text || 'Button', frame.textBinding);
    button.disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
    button.addEventListener('click', () => sendA2uiInteraction(surface, surfaceId, frame.id, 'click', true));
    parent.appendChild(button);
}

function renderA2uiInput(parent, frame, surfaceId, surface) {
    const input = document.createElement('input');
    input.className = 'a2ui-control';
    input.placeholder = frame.placeholder || frame.label || '';
    input.value = String(boundValue(surface, frame, frame.value || '') ?? '');
    input.disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
    setSurfaceValue(surface, frame, input.value);
    input.addEventListener('change', () => {
        setSurfaceValue(surface, frame, input.value);
        sendA2uiInteraction(surface, surfaceId, frame.id, 'change', input.value);
    });
    parent.appendChild(input);
}

function normalizeOptions(frame, surface) {
    const source = boundValue(surface, frame, frame.options, 'itemsBinding');
    return Array.isArray(source) ? source.map(option => {
        if (typeof option === 'string') return { label: option, value: option };
        return {
            label: option.label ?? option.text ?? option.value ?? 'option',
            value: option.value ?? option.label ?? option.text ?? 'option',
            selected: Boolean(option.selected)
        };
    }) : [];
}

function renderA2uiSelect(parent, frame, surfaceId, surface) {
    const select = document.createElement('select');
    select.className = 'a2ui-control';
    const current = boundValue(surface, frame, frame.value);
    for (const option of normalizeOptions(frame, surface)) {
        const el = document.createElement('option');
        el.value = option.value;
        el.textContent = option.label;
        el.selected = current !== undefined ? option.value === current : option.selected;
        select.appendChild(el);
    }
    select.disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
    setSurfaceValue(surface, frame, select.value);
    select.addEventListener('change', () => {
        setSurfaceValue(surface, frame, select.value);
        sendA2uiInteraction(surface, surfaceId, frame.id, 'change', select.value);
    });
    parent.appendChild(select);
}

function renderA2uiCheckBox(parent, frame, surfaceId, surface) {
    const options = normalizeOptions(frame, surface);
    if (!options.length) {
        const label = document.createElement('label');
        label.className = 'a2ui-muted';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = Boolean(boundValue(surface, frame, frame.checked ?? frame.value ?? false));
        checkbox.disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
        setSurfaceValue(surface, frame, checkbox.checked);
        checkbox.addEventListener('change', () => {
            setSurfaceValue(surface, frame, checkbox.checked);
            sendA2uiInteraction(surface, surfaceId, frame.id, 'change', checkbox.checked);
        });
        label.appendChild(checkbox);
        label.append(` ${frame.label || frame.text || ''}`);
        parent.appendChild(label);
        return;
    }
    const group = document.createElement('div');
    const disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
    const checkboxItems = [];
    const current = boundValue(surface, frame, surface.values?.[frame.id]);
    const currentValues = Array.isArray(current) ? current.map(value => String(value)) : null;
    const updateGroupState = () => {
        const selected = checkboxItems.filter(item => item.checkbox.checked).map(item => item.value);
        if (frame.id) surface.values[frame.id] = selected;
        setDataModelPath(surface, frame.valueBinding, selected);
        return selected;
    };
    for (const option of options) {
        const label = document.createElement('label');
        label.className = 'a2ui-muted';
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = currentValues ? currentValues.includes(String(option.value)) : Boolean(option.selected);
        checkbox.disabled = disabled;
        checkboxItems.push({ checkbox, value: option.value });
        checkbox.addEventListener('change', () => {
            const selected = updateGroupState();
            sendA2uiInteraction(surface, surfaceId, frame.id, 'change', selected);
        });
        label.appendChild(checkbox);
        label.append(` ${option.label}`);
        group.appendChild(label);
        group.appendChild(document.createElement('br'));
    }
    if (currentValues) {
        if (frame.id) surface.values[frame.id] = currentValues;
    } else {
        updateGroupState();
    }
    parent.appendChild(group);
}

function renderA2uiDateTimeInput(parent, frame, surfaceId, surface) {
    const input = document.createElement('input');
    input.className = 'a2ui-control';
    input.type = frame.inputType || (frame.mode === 'date' ? 'date' : 'datetime-local');
    input.value = String(boundValue(surface, frame, frame.value || '') ?? '');
    input.disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
    setSurfaceValue(surface, frame, input.value);
    input.addEventListener('change', () => {
        setSurfaceValue(surface, frame, input.value);
        sendA2uiInteraction(surface, surfaceId, frame.id, 'change', input.value);
    });
    parent.appendChild(input);
}

function renderA2uiContainer(parent, frame, surfaceId, surface, type) {
    const container = document.createElement('div');
    container.style.display = 'flex';
    container.style.flexDirection = type === 'Row' ? 'row' : 'column';
    container.style.gap = '0.75rem';
    renderChildComponents(container, frame, surfaceId, surface);
    parent.appendChild(container);
}

function renderChildComponents(parent, frame, surfaceId, surface) {
    const children = frame.components || frame.children || frame.items || [];
    if (!Array.isArray(children)) return;
    for (const child of children) {
        renderA2uiFrame(child, surfaceId, surface, parent);
    }
}

function renderA2uiList(parent, frame, surfaceId, surface) {
    const items = boundValue(surface, frame, frame.items || [], 'itemsBinding');
    const list = document.createElement(frame.ordered ? 'ol' : 'ul');
    for (const item of Array.isArray(items) ? items : []) {
        const li = document.createElement('li');
        if (typeof item === 'object' && item !== null) {
            renderA2uiFrame(item, surfaceId, surface, li);
        } else {
            li.textContent = String(item ?? '');
        }
        list.appendChild(li);
    }
    parent.appendChild(list);
}

function renderA2uiTabs(parent, frame, surfaceId, surface) {
    const tabs = frame.tabs || frame.items || [];
    const list = document.createElement('div');
    list.style.display = 'flex';
    list.style.gap = '0.5rem';
    const panel = document.createElement('div');
    panel.className = 'a2ui-frame';
    tabs.forEach((tab, index) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'a2ui-tab';
        button.textContent = tab.title || tab.label || `Tab ${index + 1}`;
        button.addEventListener('click', () => {
            panel.innerHTML = '';
            for (const child of tab.components || tab.children || []) renderA2uiFrame(child, surfaceId, surface, panel);
        });
        list.appendChild(button);
    });
    parent.appendChild(list);
    parent.appendChild(panel);
    list.querySelector('button')?.click();
}

function renderA2uiTable(parent, frame, _surfaceId, surface) {
    const table = document.createElement('table');
    table.className = 'a2ui-table';
    const rows = boundValue(surface, frame, frame.rows || [], 'itemsBinding');
    if (Array.isArray(frame.columns)) {
        const thead = document.createElement('thead');
        const tr = document.createElement('tr');
        for (const column of frame.columns) {
            const th = document.createElement('th');
            th.textContent = String(column.label ?? column.key ?? column);
            tr.appendChild(th);
        }
        thead.appendChild(tr);
        table.appendChild(thead);
    }
    const tbody = document.createElement('tbody');
    for (const row of Array.isArray(rows) ? rows : []) {
        const tr = document.createElement('tr');
        const cells = Array.isArray(row) ? row : frame.columns?.map(column => row?.[column.key ?? column]) ?? Object.values(row || {});
        for (const cell of cells) {
            const td = document.createElement('td');
            td.textContent = String(cell ?? '');
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    parent.appendChild(table);
}

function renderA2uiImage(parent, frame) {
    const note = document.createElement('div');
    note.className = 'a2ui-muted';
    note.textContent = frame.alt || frame.label || frame.url || 'Image URL omitted by Canvas security policy.';
    parent.appendChild(note);
}

function renderA2uiDivider(parent) {
    parent.appendChild(document.createElement('hr'));
}

function renderA2uiProgress(parent, frame, surfaceId, surface) {
    const progress = document.createElement('progress');
    progress.className = 'a2ui-progress';
    progress.max = Number(frame.max ?? 100);
    const raw = Number(boundValue(surface, frame, frame.value ?? 0));
    progress.value = raw <= 1 && progress.max === 100 ? raw * 100 : raw;
    parent.appendChild(progress);
    if (canonicalA2uiType(frame.type) === 'Slider') {
        const range = document.createElement('input');
        range.type = 'range';
        range.className = 'a2ui-control';
        range.min = Number(frame.min ?? 0);
        range.max = Number(frame.max ?? 100);
        range.value = String(progress.value);
        range.disabled = Boolean(readDataModelPath(surface, frame.disabledBinding));
        range.addEventListener('change', () => {
            setSurfaceValue(surface, frame, Number(range.value));
            sendA2uiInteraction(surface, surfaceId, frame.id, 'change', Number(range.value));
        });
        parent.appendChild(range);
    }
}

function renderA2uiChart(parent, frame, _surfaceId, surface) {
    const data = boundValue(surface, frame, frame.data || [], 'itemsBinding');
    if (!Array.isArray(data) || !data.length) {
        appendMarkdownOrText(parent, JSON.stringify(data ?? {}, null, 2), false);
        return;
    }
    const max = Math.max(...data.map(item => Number(item.value ?? item.y ?? 0)), 1);
    for (const item of data) {
        const label = document.createElement('div');
        label.className = 'a2ui-muted';
        label.textContent = String(item.label ?? item.x ?? '');
        const bar = document.createElement('div');
        bar.className = 'a2ui-chart-bar';
        bar.style.width = `${Math.max(2, (Number(item.value ?? item.y ?? 0) / max) * 100)}%`;
        parent.appendChild(label);
        parent.appendChild(bar);
    }
}

function renderA2uiIcon(parent, frame) {
    const span = document.createElement('span');
    span.className = 'a2ui-muted';
    span.textContent = frame.label || frame.name || 'Icon';
    parent.appendChild(span);
}

function renderA2uiCarousel(parent, frame, surfaceId, surface) {
    renderA2uiList(parent, { ...frame, items: frame.items || frame.slides || [] }, surfaceId, surface);
}

function renderA2uiMediaPlaceholder(parent, frame, _surfaceId, _surface, type) {
    const note = document.createElement('div');
    note.className = 'a2ui-muted';
    note.textContent = `${type} source omitted by Canvas security policy.`;
    parent.appendChild(note);
}

function renderA2uiWebFallback(parent, frame) {
    const note = document.createElement('div');
    note.className = 'a2ui-muted';
    note.textContent = frame.url ? `Web component blocked: ${frame.url}` : 'Web component blocked by Canvas security policy.';
    parent.appendChild(note);
}

function renderA2uiFallback(parent, frame, _surfaceId, surface, type) {
    parent.textContent = `Unsupported A2UI component: ${type}`;
    surface.diagnostics.push(`Unsupported A2UI component: ${type}`);
}

function sendA2uiInteraction(surface, surfaceId, componentId, eventName, value) {
    if (surface.protocolVersion === 'v0_9') {
        sendA2uiAction(surfaceId, componentId, eventName, value, syncedDataModelJson(surface));
        return;
    }
    sendA2uiEvent(surfaceId, componentId, eventName, value);
}

function sendA2uiEvent(surfaceId, componentId, eventName, value) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({
        type: 'a2ui_event',
        sessionId: canvasSessionId,
        surfaceId: surfaceId || 'main',
        componentId,
        event: eventName,
        valueJson: JSON.stringify(value ?? null),
        sequence: ++canvasEventSequence
    }));
}

function sendA2uiAction(surfaceId, componentId, action, value, dataModelJson) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({
        type: 'a2ui_action',
        operation: 'action',
        sessionId: canvasSessionId,
        surfaceId: surfaceId || 'main',
        componentId,
        action,
        valueJson: JSON.stringify(value ?? null),
        dataModelJson: dataModelJson || null,
        sequence: ++canvasEventSequence
    }));
}

function buildCanvasSnapshot(requestedSurfaceId = activeCanvasSurfaceId || 'main') {
    const surface = canvasSurfaces.get(requestedSurfaceId) || null;
    const components = surface?.components || [];
    return {
        type: 'canvas_snapshot',
        surfaceId: surface?.surfaceId || requestedSurfaceId,
        catalogId: surface?.catalogId || null,
        title: surface?.title || null,
        visible: !canvasPanel.hidden,
        frameCount: components.length,
        components,
        frames: components.map(frame => ({
            id: frame.id,
            type: frame.type,
            title: frame.title,
            label: frame.label,
            text: frame.text
        })),
        dataModelJson: surface?.dataModelJson || null,
        values: surface?.values || {},
        diagnostics: surface?.diagnostics || [],
        localHtmlText: canvasHtmlFrame.hidden ? null : canvasHtmlFrame.srcdoc
    };
}

function sendCanvasSnapshot(env) {
    if (!ws || ws.readyState !== WebSocket.OPEN) {
        appendSystem('Canvas snapshot needs an active chat connection.', true);
        return false;
    }
    const snapshot = buildCanvasSnapshot(env.surfaceId || activeCanvasSurfaceId || 'main');
    ws.send(JSON.stringify({
        type: 'canvas_snapshot_result',
        requestId: env.requestId,
        sessionId: canvasSessionId,
        surfaceId: snapshot.surfaceId,
        snapshotMode: env.snapshotMode || 'state',
        success: true,
        snapshotJson: JSON.stringify(snapshot)
    }));
    return true;
}

function handleA2uiEval(env) {
    ws.send(JSON.stringify({
        type: 'canvas_eval_result',
        requestId: env.requestId,
        sessionId: canvasSessionId,
        surfaceId: env.surfaceId || 'main',
        success: false,
        error: 'Webchat Canvas does not support a2ui_eval because browser-side script execution is disabled.'
    }));
}

function enqueueToolApproval(env) {
    approvalQueue.push(env || {});
    showNextToolApproval();
}

function showNextToolApproval() {
    if (activeApproval || approvalQueue.length === 0 || !doctorModal.hidden || !providerModal.hidden) return;
    activeApproval = approvalQueue.shift();
    const toolName = activeApproval.toolName || 'unknown';
    const approvalId = activeApproval.approvalId || '';
    approvalToolName.textContent = toolName;
    approvalIdEl.textContent = approvalId || 'missing';
    approvalArgs.textContent = activeApproval.argumentsPreview || activeApproval.argumentsJson || JSON.stringify(activeApproval.arguments || {}, null, 2);
    approvalRisk.textContent = activeApproval.riskHint || activeApproval.mutationHint || activeApproval.text || activeApproval.content || 'Review arguments before approving.';
    openModal(approvalModal, approvalApproveButton);
}

function decideToolApproval(approved) {
    if (!activeApproval) return;
    const toolName = activeApproval.toolName || 'unknown';
    const approvalId = activeApproval.approvalId || '';
    if (!approvalId) {
        appendSystem(`Tool approval decision not sent (missing approval id): ${toolName}`, true);
        activeApproval = null;
        closeModal(approvalModal, messageInput);
        showNextToolApproval();
        return;
    }
    if (!ws || ws.readyState !== WebSocket.OPEN) {
        appendSystem(`Tool approval decision not sent (disconnected): ${toolName}`, true);
        activeApproval = null;
        closeModal(approvalModal, messageInput);
        showNextToolApproval();
        return;
    }
    ws.send(JSON.stringify({
        type: 'tool_approval_decision',
        approvalId,
        approved
    }));
    appendSystem(`Tool approval ${approved ? 'approved' : 'denied'}: ${toolName}`);
    activeApproval = null;
    closeModal(approvalModal, messageInput);
    showNextToolApproval();
}

async function openDoctorDiagnostics(addToChat = false) {
    latestDoctorText = '';
    doctorOutput.textContent = 'Loading diagnostics...';
    openModal(doctorModal, doctorCopyButton);
    try {
        const token = getCurrentToken();
        const headers = {};
        if (token && token !== 'bypass') {
            headers['Authorization'] = `Bearer ${token}`;
        }
        const resp = await fetch('/doctor/text', { method: 'GET', headers: headers });
        if (!resp.ok) {
            latestDoctorText = `Doctor request failed (${resp.status}).`;
            doctorOutput.textContent = latestDoctorText;
            appendSystem(latestDoctorText, true);
            return;
        }
        latestDoctorText = await resp.text();
        doctorOutput.textContent = latestDoctorText;
        if (addToChat) {
            appendAssistantMarkdown("```\n" + latestDoctorText + "\n```");
        }
    } catch (_) {
        latestDoctorText = 'Doctor request failed.';
        doctorOutput.textContent = latestDoctorText;
        appendSystem(latestDoctorText, true);
    }
}

function connect() {
    setConnectionState(reconnectAttempts > 0 ? 'reconnecting' : 'connecting');
    appendSystem('Connecting to OpenClaw.NET Gateway...');
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const baseWsUrl = `${protocol}//${window.location.host}${getBasePath()}/ws`;
    const token = getCurrentToken();
    const wsUrl = token
        ? `${baseWsUrl}?token=${encodeURIComponent(token)}`
        : baseWsUrl;

    persistToken(tokenInput.value.trim());

    ws = new WebSocket(wsUrl);

    ws.onopen = () => {
        setConnectionState('connected');
        reconnectAttempts = 0;
        if (reconnectTimer) {
            clearTimeout(reconnectTimer);
            reconnectTimer = null;
        }
        dismissFirstRunCard();
        refreshChatState();
        appendSystem('Connected successfully.');
        sendCanvasReady();
        updateComposerAvailability();
        messageInput.focus();
        // Load sessions into sidebar
        void loadSessions();
    };

    ws.onmessage = (event) => {
        try {
            const env = JSON.parse(event.data);

            // Capture sessionId from server messages
            if (env.sessionId && !isViewingHistory) {
                if (!currentSessionId || currentSessionId !== env.sessionId) {
                    setCurrentSessionId(env.sessionId);
                }
            }

            if (isCanvasEnvelopeType(env.type)) {
                handleCanvasEnvelope(env);
                return;
            }

            switch (env.type) {
                case 'typing_start':
                    typingRow.style.display = 'flex';
                    setStopMode(true);
                    scrollToBottom();
                    break;

                case 'typing_stop':
                    typingRow.style.display = 'none';
                    setStopMode(false);
                    scheduleActiveResponseRender(true);
                    activeResponseDiv = null;
                    activeRawContent = "";
                    scrollToBottom();
                    break;

                case 'assistant_message':
                case 'assistant_chunk':
                case 'text_delta':
                    if (!activeResponseDiv) {
                        const row = createRow('assistant');
                        activeResponseDiv = document.createElement('div');
                        activeResponseDiv.className = 'message assistant';
                        addMessageMeta(activeResponseDiv, 'Assistant');
                        row.appendChild(activeResponseDiv);
                        chatContainer.insertBefore(row, typingRow);
                    }

                    activeRawContent += (env.text ?? env.content ?? "");
                    scheduleActiveResponseRender(false);
                    break;

                case 'assistant_done':
                    typingRow.style.display = 'none';
                    setStopMode(false);
                    scheduleActiveResponseRender(true);
                    activeResponseDiv = null;
                    activeRawContent = "";
                    scrollToBottom();
                    break;

                case 'error':
                    setStopMode(false);
                    typingRow.style.display = 'none';
                    appendSystem(env.text ?? env.content ?? 'An unknown error occurred.', true);
                    break;

                case 'tool_start':
                    appendToolPill(env.text ?? env.content ?? 'tool');
                    break;

                case 'tool_result':
                    if (isToolFailureEnvelope(env)) {
                        appendToolFailure(explainToolFailure(env));
                    }
                    break;

                case 'tool_approval_required': {
                    enqueueToolApproval(env);
                    break;
                }

                case 'file_attachment': {
                    const row = createRow('assistant');
                    const a = document.createElement('a');
                    a.className = 'file-attachment-msg';
                    // Support camelCase (server default) and PascalCase fallbacks.
                    const _faFileName = env.fileName || env.filename || env.FileName || env.text || 'File';
                    const _faFileUrl  = env.fileUrl  || env.url      || env.FileUrl  || '';
                    const _faMime     = env.mimeType || env.MimeType || '';
                    const _faSize     = env.fileSizeBytes ?? env.FileSizeBytes ?? null;
                    a.textContent = '\uD83D\uDCCE ' + _faFileName;
                    var _faSafeUrl = '#';
                    if (_faFileUrl) {
                        if (_faFileUrl.startsWith('/media/')) {
                            if (!/\.\./.test(_faFileUrl)) _faSafeUrl = encodeURI(_faFileUrl);
                        } else if (_faFileUrl.startsWith('http://') || _faFileUrl.startsWith('https://')) {
                            _faSafeUrl = encodeURI(_faFileUrl);
                        }
                    }
                    a.href = _faSafeUrl;
                    a.setAttribute('data-media-url', _faSafeUrl);
                    if (_faSize !== null) {
                        const _faSizeLabel = _faSize < 1024 ? _faSize + ' B'
                            : _faSize < 1048576 ? (_faSize / 1024).toFixed(1) + ' KB'
                            : (_faSize / 1048576).toFixed(1) + ' MB';
                        a.title = _faFileName + ' · ' + _faSizeLabel + (_faMime ? ' · ' + _faMime : '');
                    }
                    row.appendChild(a);
                    chatContainer.insertBefore(row, typingRow);
                    scrollToBottom();
                    break;
                }

                case 'artifact': {
                    appendArtifactCard(env);
                    break;
                }
            }
        } catch (e) {
            if (!activeResponseDiv) {
                const row = createRow('assistant');
                activeResponseDiv = document.createElement('div');
                activeResponseDiv.className = 'message assistant';
                addMessageMeta(activeResponseDiv, 'Assistant');
                row.appendChild(activeResponseDiv);
                chatContainer.insertBefore(row, typingRow);
            }
            activeRawContent += event.data;
            scheduleActiveResponseRender(false);
        }
    };

    ws.onclose = (event) => {
        updateComposerAvailability();
        resetActiveResponse();

        const isAuthClose = event.code === 1008 || (event.code >= 4000 && event.code < 5000);
        if (isAuthClose && !WEBCHAT_CONFIG.retryOnAuthCloseCodes) {
            setConnectionState('auth_required');
            refreshChatState();
            appendSystem('Connection closed due to authentication/authorization. Update token and reconnect.', true);
            return;
        }

        reconnectAttempts += 1;
        if (WEBCHAT_CONFIG.maxReconnectAttempts > 0 && reconnectAttempts > WEBCHAT_CONFIG.maxReconnectAttempts) {
            setConnectionState('disconnected');
            appendSystem('Connection dropped and reconnect limit reached. Refresh to retry.', true);
            return;
        }

        const delay = Math.min(
            WEBCHAT_CONFIG.maxReconnectDelayMs,
            Math.round(
                WEBCHAT_CONFIG.initialReconnectDelayMs *
                Math.pow(WEBCHAT_CONFIG.reconnectBackoffFactor, Math.max(0, reconnectAttempts - 1))
            )
        );
        setConnectionState('reconnecting');
        appendSystem(`Connection dropped. Retrying in ${Math.max(1, Math.ceil(delay / 1000))}s...`, true);
        reconnectTimer = setTimeout(connect, delay);
    };

    ws.onerror = () => {
        console.error('WebSocket encountered an error.');
    };
}

function refreshChatStateAndReconnectIfAuthRequired() {
    refreshChatState();
    if (connectionState !== 'auth_required') return;
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }
    reconnectAttempts = 0;
    connect();
}

function readImageAsDataUrl(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result || ''));
        reader.onerror = () => reject(reader.error || new Error('Unable to read image.'));
        reader.readAsDataURL(file);
    });
}

function clearImageSelection() {
    imageInput.value = '';
    attachButton.classList.remove('has-files');
    updateAttachmentSummary();
}

function updateAttachmentSummary() {
    const files = Array.from(imageInput.files || []);
    attachmentSummary.classList.toggle('active', files.length > 0);
    attachmentCount.textContent = files.length ? `${files.length} image${files.length === 1 ? '' : 's'} selected` : '';
    attachmentList.textContent = files.map(file => file.name).join(', ');
}

function setStopMode(active) {
    isAwaitingResponse = active;
    if (active) {
        sendButton.classList.add('stop-mode');
        sendButton.disabled = false;
        sendButton.title = 'Stop generation';
        messageInput.disabled = true;
    } else {
        sendButton.classList.remove('stop-mode');
        sendButton.title = 'Send message';
        if (ws && ws.readyState === WebSocket.OPEN) {
            messageInput.disabled = false;
            sendButton.disabled = false;
            messageInput.focus();
        }
    }
}

async function sendMessage() {
    const text = messageInput.value.trim();
    const imageFiles = Array.from(imageInput.files || []);
    const hasFiles = pendingFileUrls.length > 0;
    if (!text && imageFiles.length === 0 && !hasFiles) return;
    if (isLiveMode()) {
        if (!isLiveConnected()) {
            appendSystem('Start a live session before sending live messages.', true);
            return;
        }
        if (imageFiles.length > 0 || hasFiles) {
            appendSystem('File attachments are available in Chat mode.', true);
            clearImageSelection();
            return;
        }
    } else if (!ws || ws.readyState !== WebSocket.OPEN) {
        return;
    }

    let outboundText = text;
    if (!isLiveMode()) {
        outboundText = text || (imageFiles.length > 0 ? 'Describe this image.' : '');
        try {
            for (const file of imageFiles) {
                outboundText += `\n[IMAGE_URL:${await readImageAsDataUrl(file)}]`;
            }
        } catch (error) {
            appendSystem(error.message || 'Unable to read image attachment.', true);
            clearImageSelection();
            return;
        }
        // Append pending file URLs (non-image uploads)
        for (const fileRef of pendingFileUrls) {
            outboundText += `\n[FILE_URL:${fileRef.url}]`;
        }
    }

    const row = createRow('user');
    const div = document.createElement('div');
    div.className = 'message user';
    const displayParts = [];
    if (text) displayParts.push(text);
    if (imageFiles.length > 0) displayParts.push(`${imageFiles.length} image${imageFiles.length === 1 ? '' : 's'}`);
    if (pendingFileUrls.length > 0) {
        for (const f of pendingFileUrls) {
            const a = document.createElement('a');
            a.className = 'file-attachment-msg';
            a.textContent = '📎 ' + f.name;
            var safeUrl = f.url && /^(https?:|\/media\/)/i.test(f.url) ? f.url : '#';
            a.href = safeUrl;
            a.setAttribute('data-media-url', safeUrl);
            div.appendChild(a);
        }
    }
    if (displayParts.length > 0) {
        const t = document.createElement('div');
        t.textContent = displayParts.join(' · ');
        div.insertBefore(t, div.firstChild);
    } else if (pendingFileUrls.length === 0 && imageFiles.length > 0) {
        div.textContent = `${imageFiles.length} image attachment${imageFiles.length === 1 ? '' : 's'}`;
    }
    addMessageMeta(div, 'User');
    row.appendChild(div);

    chatContainer.insertBefore(row, typingRow);
    updateEmptyState();

    if (isLiveMode()) {
        liveWs.send(JSON.stringify({
            type: "text",
            text: text,
            turnComplete: true
        }));
        typingRow.style.display = 'flex';
    } else {
        const sessionId = ensureCurrentSessionId();
        ws.send(JSON.stringify({
            type: "user_message",
            text: outboundText,
            sessionId
        }));
    }

    messageInput.value = '';
    clearImageSelection();
    clearPendingFiles();
    messageInput.style.height = 'auto';
    sendButton.style.opacity = '0.7';
    activeResponseDiv = null;
    activeRawContent = "";
    scrollToBottom();
    
    // Re-focus input after send (optional but good UX for desktop)
    messageInput.focus();
}

messageInput.addEventListener('input', function () {
    this.style.height = 'auto';
    this.style.height = Math.min(this.scrollHeight, 200) + 'px';
    if (this.value === '') this.style.height = 'auto';

    if (this.value.trim().length > 0) {
        sendButton.style.opacity = '1';
        sendButton.style.transform = 'scale(1)';
    } else {
        sendButton.style.opacity = '0.7';
        sendButton.style.transform = 'scale(0.95)';
    }
});

messageInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        void sendMessage();
    }
});

sendButton.addEventListener('click', () => {
    if (isAwaitingResponse) {
        void stopCurrentExecution();
        setStopMode(false);
        typingRow.style.display = 'none';
        if (activeResponseDiv) {
            scheduleActiveResponseRender(true);
            activeResponseDiv = null;
            activeRawContent = '';
        }
    } else {
        void sendMessage();
    }
});

async function stopCurrentExecution() {
    if (currentSessionId) {
        try {
            const headers = await getAuthHeaders();
            const response = await fetch(getBasePath() + '/admin/sessions/' + encodeURIComponent(currentSessionId) + '/abort', {
                method: 'POST',
                headers
            });

            if (response.ok) {
                return;
            }
        } catch (_) {
            // Fall back to the chat command below.
        }
    }

    if (ws && ws.readyState === WebSocket.OPEN) {
        const stopMsg = { type: 'user_message', text: '/stop' };
        if (currentSessionId) stopMsg.sessionId = currentSessionId;
        ws.send(JSON.stringify(stopMsg));
    }
}
attachButton.addEventListener('click', () => imageInput.click());
imageInput.addEventListener('change', () => {
    attachButton.classList.toggle('has-files', (imageInput.files || []).length > 0);
    updateAttachmentSummary();
});
clearAttachmentsButton.addEventListener('click', clearImageSelection);
modeSelect.addEventListener('change', () => {
    if (!isLiveMode() && (liveWs || liveMediaStream)) {
        disconnectLive();
        appendSystem('Live session stopped after switching to Chat mode.');
    }
    updateComposerAvailability();
    renderRuntimeStatus();
});
liveProviderSelect.addEventListener('change', updateComposerAvailability);
speechProviderSelect.addEventListener('change', updateComposerAvailability);
liveConnectButton.addEventListener('click', connectLive);
liveInterruptButton.addEventListener('click', () => {
    if (isLiveConnected()) {
        liveWs.send(JSON.stringify({ type: 'interrupt' }));
    }
});
liveMicButton.addEventListener('click', async () => {
    if (liveMediaStream) {
        stopLiveMicCapture(true);
    } else {
        try {
            await startLiveMicCapture();
        } catch (error) {
            appendSystem(error.message || 'Unable to access the microphone.', true);
            stopLiveMicCapture(false);
        }
    }
});

const sessionToken = sessionStorage.getItem(TOKEN_KEY_SESSION);
const persistedToken = localStorage.getItem(TOKEN_KEY_PERSIST);
if (sessionToken) {
    tokenInput.value = sessionToken;
} else if (persistedToken) {
    tokenInput.value = persistedToken;
    rememberToken.checked = true;
}

rememberToken.addEventListener('change', () => {
    const token = tokenInput.value.trim();
    if (rememberToken.checked && token) {
        localStorage.setItem(TOKEN_KEY_PERSIST, token);
    } else if (!rememberToken.checked) {
        localStorage.removeItem(TOKEN_KEY_PERSIST);
    }
    refreshChatStateAndReconnectIfAuthRequired();
});
tokenInput.addEventListener('change', refreshChatStateAndReconnectIfAuthRequired);
tokenInput.addEventListener('blur', refreshChatStateAndReconnectIfAuthRequired);

// Settings drawer + provider modal + theme + first-run wiring.
settingsButton?.addEventListener('click', openSettingsDrawer);
settingsCloseButton?.addEventListener('click', closeSettingsDrawer);
settingsBackdrop?.addEventListener('click', closeSettingsDrawer);
reconnectButton?.addEventListener('click', () => {
    if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }
    reconnectAttempts = 0;
    if (ws && ws.readyState !== WebSocket.CLOSED) {
        try { ws.close(); } catch (_) { /* ignore */ }
    }
    connect();
});
connectionBannerAction?.addEventListener('click', () => {
    reconnectAttempts = 0;
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    connect();
});
authBannerAction?.addEventListener('click', openSettingsDrawer);
connectionPill?.addEventListener('click', openSettingsDrawer);

themeToggleButton?.addEventListener('click', toggleTheme);
themeToggleGroup.forEach((btn) => {
    btn.addEventListener('click', () => setTheme(btn.dataset.themeValue));
});

firstRunConnectProvider?.addEventListener('click', openProviderModal);
drawerConnectProvider?.addEventListener('click', openProviderModal);
firstRunUseDefaults?.addEventListener('click', dismissFirstRunCard);
firstRunAdvanced?.addEventListener('click', openSettingsDrawer);

providerModalClose?.addEventListener('click', closeProviderModal);
providerSkipButton?.addEventListener('click', closeProviderModal);
providerSelect?.addEventListener('change', () => {
    providerBaseUrlField.hidden = providerSelect.value !== 'openai-compatible';
});
providerSaveButton?.addEventListener('click', () => {
    const data = {
        provider: providerSelect.value,
        key: providerKeyInput.value.trim() || null,
        baseUrl: providerSelect.value === 'openai-compatible' ? (providerBaseUrlInput.value.trim() || null) : null,
        model: providerModelInput.value.trim() || null
    };
    saveProviderSetup(data);
    renderProviderStatus();
    dismissFirstRunCard();
    closeProviderModal();
    appendSystem('Provider settings saved locally.');
});

advancedClearToken?.addEventListener('click', () => {
    sessionStorage.removeItem(TOKEN_KEY_SESSION);
    sessionStorage.removeItem(OIDC_TOKEN_KEY);
    localStorage.removeItem(TOKEN_KEY_PERSIST);
    tokenInput.value = '';
    rememberToken.checked = false;
    refreshChatStateAndReconnectIfAuthRequired();
    appendSystem('Local token cleared.');
});
advancedResetUi?.addEventListener('click', () => {
    localStorage.removeItem(FIRST_RUN_DISMISSED_KEY);
    localStorage.removeItem(PROVIDER_SETUP_KEY);
    sessionStorage.removeItem(PROVIDER_KEY_SESSION);
    clearOidcConfig();
    renderProviderStatus();
    updateEmptyState();
    appendSystem('Local UI state reset.');
});

// ── OIDC config fields + auth mode switching ─────────────────────────
const oidcAuthorityInput = document.getElementById('oidc-authority-input');
const oidcClientIdInput  = document.getElementById('oidc-client-id-input');
const oidcConfigSave     = document.getElementById('oidc-config-save');
const oidcConfigClear    = document.getElementById('oidc-config-clear');

// Initialise auth mode and pre-populate OIDC fields from localStorage (or built-in defaults)
(function initAuthMode() {
    const mode = getClientAuthMode();
    applyAuthMode(mode);
    const cfg = getOidcConfig();
    if (oidcAuthorityInput) oidcAuthorityInput.value = cfg.authority || OIDC_DEFAULT_AUTHORITY;
    if (oidcClientIdInput)  oidcClientIdInput.value  = cfg.clientId  || OIDC_DEFAULT_CLIENT_ID;
})();

// Radio button listeners — switch auth mode
document.querySelectorAll('input[name="client-auth-mode"]').forEach(radio => {
    radio.addEventListener('change', () => {
        const mode = radio.value;
        setClientAuthMode(mode);
        applyAuthMode(mode);
        // Populate OIDC fields when switching to OIDC
        if (mode === 'oidc') {
            const cfg = getOidcConfig();
            if (oidcAuthorityInput) oidcAuthorityInput.value = cfg.authority || OIDC_DEFAULT_AUTHORITY;
            if (oidcClientIdInput)  oidcClientIdInput.value  = cfg.clientId  || OIDC_DEFAULT_CLIENT_ID;
        }
        // Reconnect so the token selection changes take effect
        if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
        reconnectAttempts = 0;
        if (ws && ws.readyState !== WebSocket.CLOSED) { try { ws.close(); } catch (_) {} }
        connect();
    });
});

oidcConfigSave?.addEventListener('click', () => {
    const authority = (oidcAuthorityInput?.value || '').trim();
    const clientId  = (oidcClientIdInput?.value  || '').trim();
    if (!authority || !clientId) { appendSystem('请填写 OIDC Authority URL 和 Client ID。', true); return; }
    saveOidcConfig({ authority, clientId });
    sessionStorage.removeItem(OIDC_TOKEN_KEY);
    void initiateOidcLogin();
});
oidcConfigClear?.addEventListener('click', () => {
    clearOidcConfig();
    if (oidcAuthorityInput) oidcAuthorityInput.value = '';
    if (oidcClientIdInput)  oidcClientIdInput.value  = '';
    appendSystem('已退出 OIDC 登录，切换回 Token 模式。');
    setClientAuthMode('token');
    applyAuthMode('token');
    refreshChatStateAndReconnectIfAuthRequired();
});

document.addEventListener('keydown', (event) => {
    if (!approvalModal.hidden) {
        trapModalFocus(event, approvalModal);
    } else if (!doctorModal.hidden) {
        trapModalFocus(event, doctorModal);
    } else if (!providerModal.hidden) {
        trapModalFocus(event, providerModal);
    } else if (!settingsDrawer.hidden) {
        trapModalFocus(event, settingsDrawer);
    }
    if (event.key !== 'Escape') return;
    if (!approvalModal.hidden) {
        decideToolApproval(false);
        return;
    }
    if (!doctorModal.hidden) {
        closeModal(doctorModal, doctorButton);
        showNextToolApproval();
        return;
    }
    if (!providerModal.hidden) {
        closeProviderModal();
        return;
    }
    if (!settingsDrawer.hidden) {
        closeSettingsDrawer();
        return;
    }
});

approvalApproveButton.addEventListener('click', () => decideToolApproval(true));
approvalDenyButton.addEventListener('click', () => decideToolApproval(false));
approvalCloseButton.addEventListener('click', () => decideToolApproval(false));

doctorButton.addEventListener('click', () => void openDoctorDiagnostics(false));
doctorCloseButton.addEventListener('click', () => {
    closeModal(doctorModal, doctorButton);
    showNextToolApproval();
});
doctorCopyButton.addEventListener('click', async () => {
    try {
        await navigator.clipboard.writeText(latestDoctorText || doctorOutput.textContent || '');
        doctorCopyButton.textContent = 'Copied';
        setTimeout(() => { doctorCopyButton.textContent = 'Copy'; }, 1200);
    } catch (_) {
        doctorCopyButton.textContent = 'Copy failed';
    }
});
doctorAddChatButton.addEventListener('click', () => {
    if (latestDoctorText) appendAssistantMarkdown("```\n" + latestDoctorText + "\n```");
});

document.querySelectorAll('.suggested-prompt').forEach((button) => {
    button.addEventListener('click', () => {
        if (button.dataset.action === 'doctor') {
            void openDoctorDiagnostics(false);
            return;
        }
        messageInput.value = button.dataset.prompt || '';
        messageInput.dispatchEvent(new Event('input'));
        messageInput.focus();
    });
});

canvasHideButton.addEventListener('click', hideCanvas);
statusCanvas.addEventListener('click', () => {
    if (canvasPanel.hidden) showCanvas();
});
canvasResetButton.addEventListener('click', resetA2ui);
canvasSnapshotButton.addEventListener('click', () => {
    const snapshot = buildCanvasSnapshot(activeCanvasSurfaceId || 'main');
    appendAssistantMarkdown("```json\n" + JSON.stringify(snapshot, null, 2) + "\n```");
    appendSystem('Canvas snapshot captured locally.');
});

applyTheme(getPreferredTheme());
renderProviderStatus();
refreshChatState();
// Handle OIDC redirect callback (code + state in URL) before connecting
handleOidcCallback().catch(() => {}).finally(() => {
    connect();
    updateComposerAvailability();
    updateAttachmentSummary();
    updateCanvasMetadata();
    updateEmptyState();
    updateConnectionBanner();
});

// ============================================================
// MEDIA DOWNLOAD — intercept /media/ clicks, send Bearer token
// ============================================================
chatContainer.addEventListener('click', async (e) => {
    // Match both explicit data-media-url elements and plain markdown-rendered /media/ links.
    const a = e.target.closest('[data-media-url], a[href]');
    if (!a) return;
    const url = (a.getAttribute('data-media-url') || a.getAttribute('href') || '').trim();
    if (!url.startsWith('/media/')) return;
    e.preventDefault();
    try {
        const headers = await getAuthHeaders();
        const resp = await fetch(url, { headers });
        if (!resp.ok) { appendSystem('Download failed: ' + resp.status, true); return; }
        const blob = await resp.blob();
        const objUrl = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = objUrl;
        const disp = resp.headers.get('Content-Disposition') || '';
        const match = disp.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
        const rawName = match ? match[1].replace(/['"]/g, '').trim() : '';
        // Decode URI-encoded filename (server uses Uri.EscapeDataString); fall back to URL segment.
        link.download = rawName ? decodeURIComponent(rawName) : (url.split('/').pop() || 'download');
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        setTimeout(() => URL.revokeObjectURL(objUrl), 30000);
    } catch (err) {
        appendSystem('Download failed: ' + err.message, true);
    }
});

// ============================================================
// NON-IMAGE FILE UPLOAD
// ============================================================
function clearPendingFiles() {
    pendingFileUrls = [];
    fileInput.value = '';
    renderFileChips();
}

function renderFileChips() {
    fileChips.innerHTML = '';
    if (!pendingFileUrls.length) {
        fileChips.hidden = true;
        return;
    }
    fileChips.hidden = false;
    for (let i = 0; i < pendingFileUrls.length; i++) {
        const f = pendingFileUrls[i];
        const chip = document.createElement('span');
        chip.className = 'file-chip';
        chip.textContent = '📎 ' + f.name;
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'file-chip__remove';
        removeBtn.title = '移除';
        removeBtn.innerHTML = '&#x2715;';
        const idx = i;
        removeBtn.addEventListener('click', () => {
            pendingFileUrls.splice(idx, 1);
            renderFileChips();
        });
        chip.appendChild(removeBtn);
        fileChips.appendChild(chip);
    }
}

async function uploadFilesToMedia(files) {
    const results = [];
    for (const file of files) {
        try {
            const headers = await getAuthHeaders();
            const formData = new FormData();
            formData.append('file', file);
            const resp = await fetch(getBasePath() + '/media/upload', { method: 'POST', headers, body: formData });
            if (!resp.ok) {
                appendSystem('File upload failed (' + resp.status + '): ' + file.name, true);
                continue;
            }
            const data = await resp.json().catch(() => null);
            var rawUrl = data?.url || data?.mediaUrl || ('/media/' + (data?.id || data?.mediaId || ''));
            if (rawUrl && !/^(https?:|\/media\/)/i.test(rawUrl)) rawUrl = '#';
            results.push({ name: file.name, url: rawUrl });
        } catch (err) {
            appendSystem('File upload error: ' + err.message, true);
        }
    }
    return results;
}

if (attachFileBtn && fileInput) {
    attachFileBtn.addEventListener('click', () => fileInput.click());
    fileInput.addEventListener('change', async () => {
        const files = Array.from(fileInput.files || []);
        if (!files.length) return;
        appendSystem('上传文件中...');
        const uploaded = await uploadFilesToMedia(files);
        pendingFileUrls.push(...uploaded);
        renderFileChips();
        fileInput.value = '';
        if (uploaded.length) appendSystem(`已上传 ${uploaded.length} 个文件`);
    });
}

// ============================================================
// ARTIFACT CARD
// ============================================================
function appendArtifactCard(env) {
    const wrapper = document.createElement('div');
    wrapper.className = 'artifact-card';

    const header = document.createElement('div');
    header.className = 'artifact-header';

    const icon = document.createElement('span');
    icon.className = 'artifact-icon';
    const type = (env.artifactType || env.type || '').toLowerCase();
    icon.textContent = type.includes('image') ? '🖼️' : type.includes('table') || type.includes('csv') ? '📊' : type.includes('code') ? '💻' : '📄';

    const title = document.createElement('span');
    title.className = 'artifact-title';
    title.textContent = env.title || env.filename || env.name || 'Artifact';
    title.title = title.textContent;

    header.appendChild(icon);
    header.appendChild(title);

    if (env.url) {
        const dlBtn = document.createElement('button');
        dlBtn.className = 'artifact-download';
        dlBtn.textContent = '下载';
        dlBtn.type = 'button';
        dlBtn.setAttribute('data-media-url', env.url);
        dlBtn.addEventListener('click', async () => {
            var dlUrl = env.url;
            // Only allow /media/ paths without traversal or external http/https URLs.
            if (!dlUrl) {
                appendSystem('Download failed: missing URL', true);
                return;
            }
            var isSameOrigin = dlUrl.startsWith('/media/') && !/\.\./.test(dlUrl);
            var isExternal = /^https?:\/\//i.test(dlUrl);
            if (!isSameOrigin && !isExternal) {
                appendSystem('Download failed: unsafe URL', true);
                return;
            }
            // Only attach auth headers for same-origin /media/ requests to avoid
            // leaking bearer tokens to external hosts.
            const fetchOpts = isSameOrigin ? { headers: await getAuthHeaders() } : {};
            const resp = await fetch(dlUrl, fetchOpts);
            if (!resp.ok) { appendSystem('Download failed: ' + resp.status, true); return; }
            const blob = await resp.blob();
            const objUrl = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = objUrl; a.download = env.filename || env.name || 'artifact';
            document.body.appendChild(a); a.click(); document.body.removeChild(a);
            setTimeout(() => URL.revokeObjectURL(objUrl), 30000);
        });
        header.appendChild(dlBtn);
    }

    wrapper.appendChild(header);

    const body = document.createElement('div');
    body.className = 'artifact-body';
    const content = env.content || env.text || '';
    if (type.includes('table') || type.includes('csv')) {
        try {
            const lines = String(content).trim().split('\n');
            const table = document.createElement('table');
            table.className = 'artifact-table';
            lines.forEach((line, i) => {
                const tr = document.createElement('tr');
                const cells = line.split(',').map(c => c.trim());
                cells.forEach(cell => {
                    const el = document.createElement(i === 0 ? 'th' : 'td');
                    el.textContent = cell;
                    tr.appendChild(el);
                });
                table.appendChild(tr);
            });
            body.appendChild(table);
        } catch (_) {
            const pre = document.createElement('pre'); pre.textContent = content; body.appendChild(pre);
        }
    } else if (content) {
        const pre = document.createElement('pre'); pre.textContent = content; body.appendChild(pre);
    }
    wrapper.appendChild(body);

    const row = createRow('assistant');
    row.appendChild(wrapper);
    chatContainer.insertBefore(row, typingRow);
    scrollToBottom();
}

// ============================================================
// SESSION SIDEBAR
// ============================================================
async function loadSessions() {
    try {
        const headers = await getAuthHeaders();
        const resp = await fetch(getBasePath() + '/admin/sessions?pageSize=60', { headers });
        if (!resp.ok) return;
        const data = await resp.json();
        // API returns { active: [...], persisted: { items: [...] } }
        const active = data.active || [];
        const persisted = (data.persisted && data.persisted.items) || data.items || data.sessions || [];
        // Merge, deduplicate (active entries may repeat in persisted), sort newest first
        const all = [
            ...active.map(s => ({ ...s, isActive: true })),
            ...persisted.map(s => ({ ...s, isActive: !!s.isActive }))
        ];
        const seen = new Set();
        allSessions = all.filter(s => { if (seen.has(s.id)) return false; seen.add(s.id); return true; });
        allSessions.sort((a, b) => new Date(b.lastActiveAt || b.updatedAt || b.createdAt || 0) - new Date(a.lastActiveAt || a.updatedAt || a.createdAt || 0));
        renderSessionList(allSessions);
    } catch (_) { /* silently fail */ }
}

function renderSessionList(sessions) {
    sessionList.innerHTML = '';
    const query = sessionSearchInput ? sessionSearchInput.value.trim().toLowerCase() : '';
    const filtered = query
        ? sessions.filter(s => {
            const title = (s.title || s.id || '').toLowerCase();
            const preview = (s.preview || s.lastMessage || '').toLowerCase();
            return title.includes(query) || preview.includes(query);
        })
        : sessions;

    if (!filtered.length) {
        const empty = document.createElement('div');
        empty.className = 'session-empty';
        empty.textContent = query ? '没有匹配的会话' : '暂无历史会话';
        sessionList.appendChild(empty);
        return;
    }

    // Already sorted in loadSessions; preserve order here
    for (const session of filtered) {
        const item = document.createElement('div');
        item.className = 'session-item' + (session.id === currentSessionId ? ' active' : '');
        item.dataset.sessionId = session.id;

        const body = document.createElement('div');
        body.className = 'session-item__body';

        const titleEl = document.createElement('div');
        titleEl.className = 'session-item__title';
        // API has no title field; derive a readable label from the id
        const label = session.title
            || (session.id === 'main' ? '主会话' : null)
            || (() => {
                // "websocket:0HNM53UGROGO0" → show channel + short id
                const parts = session.id.split(':');
                if (parts.length === 2) return `[${parts[0]}] ${parts[1].slice(-8)}`;
                return session.id.length > 20 ? session.id.slice(0, 18) + '…' : session.id;
            })();
        titleEl.textContent = (session.isActive ? '● ' : '') + label;
        titleEl.title = session.id;

        const meta = document.createElement('div');
        meta.className = 'session-item__meta';
        const ts = session.lastActiveAt || session.updatedAt || session.createdAt;
        if (ts) {
            const d = new Date(ts);
            meta.textContent = d.toLocaleDateString('zh-CN') + ' ' + d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
        }
        if (session.historyTurns != null) {
            meta.textContent += (meta.textContent ? ' · ' : '') + session.historyTurns + ' 轮';
        }

        body.appendChild(titleEl);
        body.appendChild(meta);

        const delBtn = document.createElement('button');
        delBtn.className = 'session-item__del';
        delBtn.type = 'button';
        delBtn.title = '删除会话';
        delBtn.innerHTML = '&#x2715;';
        delBtn.addEventListener('click', async (e) => {
            e.stopPropagation();
            if (!confirm('确定删除此会话？')) return;
            await deleteSession(session.id);
        });

        item.appendChild(body);
        item.appendChild(delBtn);

        item.addEventListener('click', () => void switchToSession(session.id, label));
        sessionList.appendChild(item);
    }
}

async function switchToSession(sessionId, title) {
    if (isViewingHistory && currentSessionId === sessionId) return;

    try {
        const headers = await getAuthHeaders();
        const resp = await fetch(getBasePath() + '/admin/sessions/' + encodeURIComponent(sessionId), { headers });
        if (!resp.ok) { appendSystem('Failed to load session: ' + resp.status, true); return; }
        const data = await resp.json();
        const history = data.session?.history || data.history || [];

        // Render session history into chat
        chatContainer.innerHTML = '';
        const chatStateBarClone = document.getElementById('chat-state-bar');
        if (chatStateBarClone) chatContainer.appendChild(chatStateBarClone);
        const emptyStateEl = document.getElementById('empty-state');
        if (emptyStateEl) chatContainer.appendChild(emptyStateEl);
        chatContainer.appendChild(typingRow);

        for (const turn of history) {
            if (!turn.role || (!turn.content && (!turn.toolCalls || !turn.toolCalls.length))) continue;
            if (turn.role === 'user') {
                const row = createRow('user');
                const div = document.createElement('div');
                div.className = 'message user';
                // Strip internal transport markers before displaying user message text.
                const _userText = (turn.content || '')
                    .replace(/^\[FILE_URL:[^\]]+\]\n?/gm, '')
                    .replace(/^\[IMAGE_URL:[^\]]+\]\n?/gm, '')
                    .replace(/^\[FILE_PATH:[^\]]+\]\n?/gm, '')
                    .trim();
                div.textContent = _userText;
                addMessageMeta(div, 'User');
                row.appendChild(div);
                chatContainer.insertBefore(row, document.getElementById('typing-row'));
            } else if (turn.role === 'assistant') {
                if (turn.content) {
                    appendAssistantMarkdown(turn.content);
                }
                if (turn.toolCalls) {
                    for (const tc of turn.toolCalls) {
                        appendToolPill(tc.toolName || 'tool');
                    }
                }
            }
        }

        isViewingHistory = true;
        setCurrentSessionId(sessionId);
        historyBanner.hidden = false;
        historyBannerText.textContent = '正在查看历史会话：' + (title || sessionId);
        updateEmptyState();
        scrollToBottom();

        // Update active state in sidebar
        document.querySelectorAll('.session-item').forEach(el => {
            el.classList.toggle('active', el.dataset.sessionId === sessionId);
        });
    } catch (err) {
        appendSystem('Failed to load session: ' + err.message, true);
    }
}

function returnToCurrentSession() {
    isViewingHistory = false;
    setCurrentSessionId(null);
    historyBanner.hidden = true;
    // Clear chat
    chatContainer.innerHTML = '';
    const chatStateBarEl = document.createElement('div');
    chatStateBarEl.id = 'chat-state-bar';
    chatStateBarEl.hidden = true;
    chatStateBarEl.setAttribute('aria-hidden', 'true');
    chatContainer.appendChild(chatStateBarEl);
    const emptyEl = document.getElementById('empty-state');
    if (emptyEl) chatContainer.appendChild(emptyEl);
    chatContainer.appendChild(typingRow);
    updateEmptyState();
    appendSystem('已返回当前会话。');
    document.querySelectorAll('.session-item').forEach(el => el.classList.remove('active'));
}

async function startNewChatSession() {
    if (isViewingHistory) returnToCurrentSession();
    // Generate a fresh client-side session UUID so the server creates a new conversation.
    setCurrentSessionId(`session-${createSessionUuid()}`);
    chatContainer.innerHTML = '';
    const emptyEl = document.getElementById('empty-state');
    if (emptyEl) chatContainer.appendChild(emptyEl);
    chatContainer.appendChild(typingRow);
    appendSystem('新对话已开始。');
    updateEmptyState();
    if (sessionSidebar && !sessionSidebar.hidden) {
        await loadSessions();
    }
}

async function deleteSession(sessionId) {
    try {
        const headers = await getAuthHeaders();
        const resp = await fetch(getBasePath() + '/admin/sessions/' + encodeURIComponent(sessionId), {
            method: 'DELETE', headers
        });
        if (!resp.ok) { appendSystem('删除失败: ' + resp.status, true); return; }
        allSessions = allSessions.filter(s => s.id !== sessionId);
        if (currentSessionId === sessionId) {
            returnToCurrentSession();
        }
        renderSessionList(allSessions);
        appendSystem('会话已删除');
    } catch (err) {
        appendSystem('删除失败: ' + err.message, true);
    }
}

// Session sidebar event wiring
if (sessionToggleBtn && sessionSidebar) {
    sessionToggleBtn.addEventListener('click', () => {
        const hidden = sessionSidebar.hidden;
        sessionSidebar.hidden = !hidden;
        if (!hidden) return;
        void loadSessions();
    });
}
if (sessionSidebarClose && sessionSidebar) {
    sessionSidebarClose.addEventListener('click', () => { sessionSidebar.hidden = true; });
}
if (newChatBtn) {
    newChatBtn.addEventListener('click', () => void startNewChatSession());
}
if (historyBannerClose) {
    historyBannerClose.addEventListener('click', returnToCurrentSession);
}
if (sessionSearchInput) {
    sessionSearchInput.addEventListener('input', () => renderSessionList(allSessions));
}
if (clearAllSessionsBtn) {
    clearAllSessionsBtn.addEventListener('click', async () => {
        if (!confirm('确定清空所有历史会话？此操作不可撤销。')) return;
        for (const s of [...allSessions]) {
            await deleteSession(s.id);
        }
        allSessions = [];
        renderSessionList([]);
    });
}

// ============================================================
// MCP SERVERS PANEL
// ============================================================
(() => {
    const overlay     = document.getElementById('mcp-overlay');
    const openBtn     = document.getElementById('mcp-panel-btn');
    const closeBtn    = document.getElementById('mcp-close-btn');
    const serverList  = document.getElementById('mcp-server-list');
    const formSection = document.getElementById('mcp-form-section');
    const formTitle   = document.getElementById('mcp-form-title');
    const formError   = document.getElementById('mcp-form-error');
    const addBtn      = document.getElementById('mcp-add-btn');
    const cancelBtn   = document.getElementById('mcp-cancel-btn');
    const saveBtn     = document.getElementById('mcp-save-btn');
    const statusBar   = document.getElementById('mcp-panel-status');
    const fId             = document.getElementById('mcp-f-id');
    const fName           = document.getElementById('mcp-f-name');
    const fTransport      = document.getElementById('mcp-f-transport');
    const fUrl            = document.getElementById('mcp-f-url');
    const fToken          = document.getElementById('mcp-f-token');
    const fHeadersRows    = document.getElementById('mcp-f-headers-rows');
    const fAddHeaderBtn   = document.getElementById('mcp-f-add-header-btn');
    const fPrefix         = document.getElementById('mcp-f-prefix');
    const fStartupTimeout = document.getElementById('mcp-f-startup-timeout');
    const fRequestTimeout = document.getElementById('mcp-f-request-timeout');
    const fEnabled        = document.getElementById('mcp-f-enabled');

    if (!overlay) return;

    let mcpConfig     = { Enabled: true, Servers: {} };
    let builtinConfig = { Enabled: false, Servers: {} };
    let editingId     = null;

    // ── Headers editor helpers ──────────────────────────────────────────
    function clearHeaderRows() { if (fHeadersRows) fHeadersRows.innerHTML = ''; }

    function addHeaderRow(key, value, disabled) {
        if (!fHeadersRows) return;
        const row = document.createElement('div');
        row.style.cssText = 'display:flex;gap:0.25rem;margin-top:0.2rem';

        const kInput = document.createElement('input');
        kInput.type = 'text'; kInput.className = 'input';
        kInput.placeholder = 'Header name'; kInput.value = key || ''; kInput.autocomplete = 'off';
        kInput.style.flex = '1'; kInput.disabled = !!disabled;

        const vInput = document.createElement('input');
        vInput.type = 'text'; vInput.className = 'input';
        vInput.placeholder = 'Value'; vInput.value = value || ''; vInput.autocomplete = 'off';
        vInput.style.flex = '1'; vInput.disabled = !!disabled;

        const delBtn = document.createElement('button');
        delBtn.type = 'button'; delBtn.className = 'btn btn-ghost btn-sm'; delBtn.title = 'Remove';
        delBtn.disabled = !!disabled;
        delBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
        delBtn.addEventListener('click', () => row.remove());

        row.append(kInput, vInput, delBtn);
        fHeadersRows.appendChild(row);
    }

    function getHeadersFromRows() {
        if (!fHeadersRows) return null;
        const result = {};
        fHeadersRows.querySelectorAll('div').forEach(row => {
            const inputs = row.querySelectorAll('input');
            if (inputs.length >= 2) {
                const k = inputs[0].value.trim(), v = inputs[1].value.trim();
                if (k) result[k] = v;
            }
        });
        return Object.keys(result).length ? result : null;
    }

    function setHeadersFromConfig(headers, disabled) {
        clearHeaderRows();
        if (headers && typeof headers === 'object') {
            for (const [k, v] of Object.entries(headers)) addHeaderRow(k, v, disabled);
        }
    }

    function setAllFormDisabled(disabled) {
        [fId, fName, fTransport, fUrl, fToken, fPrefix, fStartupTimeout, fRequestTimeout, fEnabled]
            .forEach(el => { if (el) el.disabled = disabled; });
        if (fAddHeaderBtn) fAddHeaderBtn.style.display = disabled ? 'none' : '';
        if (fHeadersRows) fHeadersRows.querySelectorAll('input, button').forEach(el => { el.disabled = disabled; });
    }

    if (fAddHeaderBtn) fAddHeaderBtn.addEventListener('click', () => addHeaderRow('', '', false));

    // ── Normalize camelCase → PascalCase (from ASP.NET JSON) ─────────────
    function normalizePascal(obj) {
        if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return obj;
        const out = {};
        for (const [k, v] of Object.entries(obj)) {
            const key = k.charAt(0).toUpperCase() + k.slice(1);
            out[key] = (v && typeof v === 'object' && !Array.isArray(v)) ? normalizePascal(v) : v;
        }
        return out;
    }

    function showStatus(msg, isErr) {
        statusBar.textContent = msg;
        statusBar.className = 'panel-status ' + (isErr ? 'err' : 'ok');
        statusBar.hidden = false;
        clearTimeout(showStatus._t);
        if (!isErr) showStatus._t = setTimeout(() => { statusBar.hidden = true; }, 3000);
    }

    // ── Build a server card ───────────────────────────────────────────────
    function buildCard(id, cfg, builtin) {
        const card = document.createElement('div');
        card.className = 'mcp-server-card' + (cfg.Enabled === false ? ' disabled' : '');

        const info = document.createElement('div');
        info.className = 'mcp-server-info';
        const nameEl = document.createElement('div'); nameEl.className = 'mcp-server-name'; nameEl.textContent = cfg.Name || id;
        const urlEl  = document.createElement('div'); urlEl.className  = 'mcp-server-url';  urlEl.textContent  = cfg.Url || cfg.Command || '';
        info.appendChild(nameEl); info.appendChild(urlEl);

        const badge = document.createElement('span');
        badge.className = 'mcp-server-badge' + (builtin ? ' builtin' : '');
        badge.textContent = builtin
            ? (cfg.Enabled === false ? 'builtin · off' : 'builtin')
            : (cfg.Enabled === false ? 'disabled' : (cfg.Transport || 'http'));

        const actions = document.createElement('div');
        actions.className = 'mcp-server-actions';

        if (!builtin) {
            const isEnabled = cfg.Enabled !== false;
            const toggleBtn = document.createElement('button');
            toggleBtn.className = 'panel-icon-btn'; toggleBtn.title = isEnabled ? 'Disable' : 'Enable';
            toggleBtn.innerHTML = isEnabled
                ? '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="8" y1="12" x2="16" y2="12"/></svg>'
                : '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="16"/><line x1="8" y1="12" x2="16" y2="12"/></svg>';
            toggleBtn.addEventListener('click', () => {
                mcpConfig.Servers[id] = Object.assign({}, cfg, { Enabled: !isEnabled });
                renderList(); void saveConfig();
            });

            const editBtn = document.createElement('button');
            editBtn.className = 'panel-icon-btn'; editBtn.title = 'Edit';
            editBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></svg>';
            editBtn.addEventListener('click', () => openForm(id));

            const delBtn = document.createElement('button');
            delBtn.className = 'panel-icon-btn danger'; delBtn.title = 'Delete';
            delBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4h6v2"/></svg>';
            delBtn.addEventListener('click', () => {
                if (!confirm('Delete "' + (cfg.Name || id) + '"?')) return;
                delete mcpConfig.Servers[id]; renderList(); void saveConfig();
            });
            actions.append(toggleBtn, editBtn, delBtn);
        } else {
            // Builtin: view-only button
            const viewBtn = document.createElement('button');
            viewBtn.className = 'panel-icon-btn'; viewBtn.title = 'View (read-only)';
            viewBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>';
            viewBtn.addEventListener('click', () => openBuiltinView(id, cfg));
            actions.appendChild(viewBtn);
        }

        card.append(info, badge, actions);
        return card;
    }

    function renderList() {
        serverList.innerHTML = '';
        const builtinIds = Object.keys(builtinConfig.Servers || {});
        const userIds    = Object.keys(mcpConfig.Servers || {});
        if (!builtinIds.length && !userIds.length) {
            const empty = document.createElement('div'); empty.className = 'mcp-server-empty';
            empty.textContent = 'No MCP servers configured.'; serverList.appendChild(empty); return;
        }
        if (builtinIds.length) {
            const lbl = document.createElement('div'); lbl.className = 'mcp-section-label'; lbl.textContent = 'Built-in';
            serverList.appendChild(lbl);
            builtinIds.forEach(id => serverList.appendChild(buildCard(id, builtinConfig.Servers[id], true)));
        }
        if (userIds.length) {
            const lbl = document.createElement('div'); lbl.className = 'mcp-section-label'; lbl.textContent = 'Workspace';
            serverList.appendChild(lbl);
            userIds.forEach(id => serverList.appendChild(buildCard(id, mcpConfig.Servers[id], false)));
        }
    }

    // ── Open builtin server (read-only) ──────────────────────────────────
    function openBuiltinView(id, cfg) {
        editingId = null;
        formTitle.textContent = 'Built-in Server (read-only)';
        formError.hidden = true;
        fId.value    = id; fId.readOnly = true;
        fName.value  = cfg.Name || '';
        fTransport.value = cfg.Transport || 'streamable-http';
        fUrl.value   = cfg.Url || cfg.Command || '';
        const authHeader = (cfg.Headers && cfg.Headers['Authorization']) || '';
        fToken.value = authHeader.startsWith('Bearer ') ? authHeader.slice(7) : authHeader;
        const extra = Object.fromEntries(Object.entries(cfg.Headers || {}).filter(([k]) => k !== 'Authorization'));
        setHeadersFromConfig(Object.keys(extra).length ? extra : null, true);
        if (!cfg.Headers && cfg.HasToken) fToken.value = '(hidden)';
        fPrefix.value = cfg.ToolNamePrefix || '';
        fStartupTimeout.value = cfg.StartupTimeoutSeconds != null ? cfg.StartupTimeoutSeconds : '';
        fRequestTimeout.value = cfg.RequestTimeoutSeconds != null ? cfg.RequestTimeoutSeconds : '';
        fEnabled.checked = cfg.Enabled !== false;
        setAllFormDisabled(true);
        saveBtn.hidden = true;
        cancelBtn.textContent = 'Close';
        formSection.hidden = false; addBtn.hidden = true;
    }

    // ── Open add/edit form ────────────────────────────────────────────────
    function openForm(serverId) {
        editingId = serverId || null;
        formTitle.textContent = serverId ? 'Edit Server' : 'Add Server';
        formError.hidden = true;
        setAllFormDisabled(false);
        fId.readOnly = !!serverId;
        saveBtn.hidden = false;
        cancelBtn.textContent = 'Cancel';
        if (serverId) {
            const cfg = mcpConfig.Servers[serverId] || {};
            fId.value = serverId; fName.value = cfg.Name || '';
            fTransport.value = cfg.Transport || 'streamable-http';
            fUrl.value = cfg.Url || '';
            const authHeader = (cfg.Headers && cfg.Headers['Authorization']) || '';
            fToken.value = authHeader.startsWith('Bearer ') ? authHeader.slice(7) : authHeader;
            const extra = Object.fromEntries(Object.entries(cfg.Headers || {}).filter(([k]) => k !== 'Authorization'));
            setHeadersFromConfig(Object.keys(extra).length ? extra : null, false);
            fPrefix.value = cfg.ToolNamePrefix || '';
            fStartupTimeout.value = cfg.StartupTimeoutSeconds != null ? cfg.StartupTimeoutSeconds : '';
            fRequestTimeout.value = cfg.RequestTimeoutSeconds != null ? cfg.RequestTimeoutSeconds : '';
            fEnabled.checked = cfg.Enabled !== false;
        } else {
            fId.value = 'streaming-' + Math.random().toString(36).slice(2, 8); fId.readOnly = false;
            fName.value = ''; fTransport.value = 'streamable-http'; fUrl.value = ''; fToken.value = '';
            clearHeaderRows();
            fPrefix.value = 'streaming_'; fStartupTimeout.value = ''; fRequestTimeout.value = '';
            fEnabled.checked = true;
        }
        formSection.hidden = false; addBtn.hidden = true;
        (serverId ? fName : fUrl).focus();
    }

    function closeForm() {
        setAllFormDisabled(false);
        saveBtn.hidden = false; cancelBtn.textContent = 'Cancel';
        formSection.hidden = true; addBtn.hidden = false;
        editingId = null; clearHeaderRows();
    }

    function buildServerConfig() {
        const id = fId.value.trim();
        if (!id) return { error: 'Server ID is required.' };
        if (!/^[\w\-\.]+$/.test(id)) return { error: 'Server ID may only contain letters, digits, hyphens, underscores and dots.' };
        const url = fUrl.value.trim();
        if (!url) return { error: 'URL is required.' };
        try { new URL(url); } catch (_) { return { error: 'URL is not valid.' }; }
        const cfg = { Transport: fTransport.value || 'streamable-http', Url: url, Enabled: fEnabled.checked };
        const name = fName.value.trim(); if (name) cfg.Name = name;
        // Merge bearer token + extra headers
        const allHeaders = {};
        const token = fToken.value.trim(); if (token) allHeaders['Authorization'] = 'Bearer ' + token;
        const extra = getHeadersFromRows(); if (extra) Object.assign(allHeaders, extra);
        if (Object.keys(allHeaders).length) cfg.Headers = allHeaders;
        const prefix = fPrefix.value.trim(); if (prefix) cfg.ToolNamePrefix = prefix;
        const st = parseInt(fStartupTimeout.value, 10); if (!isNaN(st) && st > 0) cfg.StartupTimeoutSeconds = st;
        const rt = parseInt(fRequestTimeout.value, 10); if (!isNaN(rt) && rt > 0) cfg.RequestTimeoutSeconds = rt;
        return { id, cfg };
    }

    async function loadConfig() {
        try {
            const headers = await getAuthHeaders();
            const resp = await fetch(getBasePath() + '/admin/workspace/mcp', { headers });
            if (resp.ok) {
                const data = await resp.json();
                const rawBuiltin = data.builtin || {};
                builtinConfig = { Enabled: !!(rawBuiltin.enabled ?? rawBuiltin.Enabled), Servers: {} };
                for (const [id, srv] of Object.entries(rawBuiltin.servers || rawBuiltin.Servers || {})) {
                    builtinConfig.Servers[id] = normalizePascal(srv);
                }
                const u = data.user || {};
                mcpConfig = {
                    Enabled: !!(u.Enabled ?? u.enabled ?? true),
                    Servers: Object.assign({}, u.Servers || u.servers || {})
                };
            } else {
                showStatus('Load failed (' + resp.status + ')', true);
            }
        } catch (e) { showStatus('Load failed: ' + e.message, true); }
        renderList();
    }

    async function saveConfig() {
        try {
            const headers = { 'Content-Type': 'application/json', ...(await getAuthHeaders()) };
            const resp = await fetch(getBasePath() + '/admin/workspace/mcp', {
                method: 'PUT', headers, body: JSON.stringify(mcpConfig)
            });
            if (resp.ok) showStatus('Saved \u2014 hot-reload will apply changes.', false);
            else showStatus('Save failed (' + resp.status + ')', true);
        } catch (e) { showStatus('Save failed: ' + e.message, true); }
    }

    openBtn.addEventListener('click', async () => { overlay.hidden = false; closeForm(); statusBar.hidden = true; await loadConfig(); });
    closeBtn.addEventListener('click', () => { overlay.hidden = true; closeForm(); });
    overlay.addEventListener('click', e => { if (e.target === overlay) { overlay.hidden = true; closeForm(); } });
    addBtn.addEventListener('click', () => openForm(null));
    cancelBtn.addEventListener('click', closeForm);
    saveBtn.addEventListener('click', async () => {
        const result = buildServerConfig();
        if (result.error) { formError.textContent = result.error; formError.hidden = false; return; }
        formError.hidden = true;
        mcpConfig.Servers[result.id] = result.cfg;
        renderList(); closeForm(); await saveConfig();
    });
})();

// ============================================================
// CHANNEL CONFIG PANEL
// ============================================================
(() => {
    const overlay = document.getElementById('channel-overlay');
    const openBtn = document.getElementById('channel-panel-btn');
    const closeBtn = document.getElementById('channel-close-btn');
    const statusBar = document.getElementById('ch-panel-status');
    const formError = document.getElementById('ch-form-error');
    const loadBtn = document.getElementById('channel-load-btn');
    const revertBtn = document.getElementById('channel-revert-btn');
    const saveBtn = document.getElementById('channel-save-btn');
    const fForm = document.getElementById('ch-feishu-form');
    const dForm = document.getElementById('ch-dingtalk-form');
    const wForm = document.getElementById('ch-wecom-form');
    const fAppId = document.getElementById('feishu-appid');
    const fAppSecret = document.getElementById('feishu-appsecret');
    const fAppSecretRef = document.getElementById('feishu-appsecret-ref');
    const dAppId = document.getElementById('dingtalk-appid');
    const dAppKey = document.getElementById('dingtalk-appkey');
    const dAppSecret = document.getElementById('dingtalk-appsecret');
    const wBotId = document.getElementById('wecom-botid');
    const wBotSecret = document.getElementById('wecom-botsecret');

    if (!overlay) return;

    let activeChannel = 'feishu';

    function setVis() {
        fForm.hidden = activeChannel !== 'feishu';
        dForm.hidden = activeChannel !== 'dingtalk';
        wForm.hidden = activeChannel !== 'wecom';
    }

    function showStatus(msg, isErr) {
        statusBar.textContent = msg;
        statusBar.className = 'panel-status ' + (isErr ? 'err' : 'ok');
        statusBar.hidden = false;
    }

    function val(el) { return el ? el.value.trim() : ''; }
    function set(el, v) { if (el) el.value = v ?? ''; }

    function populateFeishu(cfg) { set(fAppId, cfg.AppId ?? cfg.appId); set(fAppSecret, cfg.AppSecret ?? cfg.appSecret); set(fAppSecretRef, cfg.AppSecretRef ?? cfg.appSecretRef); }
    function buildFeishu() { const c = { enabled: true }; const v = val(fAppId); if (v) c.appId = v; const s = val(fAppSecret); if (s) c.appSecret = s; const r = val(fAppSecretRef); if (r) c.appSecretRef = r; return c; }
    function populateDingTalk(cfg) { set(dAppId, cfg.AppId ?? cfg.appId); set(dAppKey, cfg.AppKey ?? cfg.appKey); set(dAppSecret, cfg.AppSecret ?? cfg.appSecret); }
    function buildDingTalk() { const c = { enabled: true }; const a = val(dAppId); if (a) c.appId = a; const k = val(dAppKey); if (k) c.appKey = k; const s = val(dAppSecret); if (s) c.appSecret = s; return c; }
    function populateWeCom(cfg) { set(wBotId, cfg.BotId ?? cfg.botId); set(wBotSecret, cfg.BotSecret ?? cfg.botSecret); }
    function buildWeCom() { const c = { enabled: true }; const b = val(wBotId); if (b) c.botId = b; const s = val(wBotSecret); if (s) c.botSecret = s; return c; }

    async function loadChannel() {
        formError.hidden = true;
        showStatus('加载中...', false);
        try {
            const headers = await getAuthHeaders();
            const resp = await fetch(getBasePath() + '/admin/channels/' + activeChannel, { headers });
            if (!resp.ok) { showStatus('加载失败 (' + resp.status + ')', true); return; }
            const cfg = await resp.json();
            if (activeChannel === 'feishu') populateFeishu(cfg);
            if (activeChannel === 'dingtalk') populateDingTalk(cfg);
            if (activeChannel === 'wecom') populateWeCom(cfg);
            showStatus('已加载当前配置', false);
        } catch (e) { showStatus('加载失败: ' + e.message, true); }
    }

    async function saveChannel() {
        formError.hidden = true;
        let body = activeChannel === 'feishu' ? buildFeishu() : activeChannel === 'dingtalk' ? buildDingTalk() : buildWeCom();
        showStatus('保存中...', false);
        try {
            const headers = { 'Content-Type': 'application/json', ...(await getAuthHeaders()) };
            const resp = await fetch(getBasePath() + '/admin/channels/' + activeChannel + '/update', { method: 'POST', headers, body: JSON.stringify(body) });
            const data = await resp.json().catch(() => null);
            if (resp.ok) showStatus('已保存并重连渠道 ✓', false);
            else showStatus('保存失败: ' + (data?.error ?? resp.status), true);
        } catch (e) { showStatus('保存失败: ' + e.message, true); }
    }

    async function revertChannel() {
        if (!confirm('确定恢复默认配置？这将清除所有 API 保存的覆盖。')) return;
        showStatus('恢复中...', false);
        try {
            const headers = await getAuthHeaders();
            const resp = await fetch(getBasePath() + '/admin/channels/' + activeChannel + '/override', { method: 'DELETE', headers });
            const data = await resp.json().catch(() => null);
            if (resp.ok) { showStatus('已恢复默认配置 ✓', false); await loadChannel(); }
            else showStatus('恢复失败: ' + (data?.error ?? resp.status), true);
        } catch (e) { showStatus('恢复失败: ' + e.message, true); }
    }

    openBtn.addEventListener('click', async () => { overlay.hidden = false; statusBar.hidden = true; formError.hidden = true; setVis(); await loadChannel(); });
    closeBtn.addEventListener('click', () => { overlay.hidden = true; });
    overlay.addEventListener('click', e => { if (e.target === overlay) overlay.hidden = true; });
    loadBtn.addEventListener('click', loadChannel);
    saveBtn.addEventListener('click', saveChannel);
    revertBtn.addEventListener('click', revertChannel);
    document.querySelectorAll('.ch-tab').forEach(tab => {
        tab.addEventListener('click', async () => {
            document.querySelectorAll('.ch-tab').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            activeChannel = tab.dataset.channel;
            formError.hidden = true; statusBar.hidden = true;
            setVis(); await loadChannel();
        });
    });
})();

// ============================================================
// DIGITAL EMPLOYEE PANEL
// ============================================================
(() => {
    const overlay = document.getElementById('de-overlay');
    const openBtn = document.getElementById('de-panel-btn');
    const closeBtn = document.getElementById('de-close-btn');
    const statusBar = document.getElementById('de-panel-status');
    const tabList = document.getElementById('de-tab-list');
    const tabSkillPkg = document.getElementById('de-tab-skill-pkg');
    const tabPkg = document.getElementById('de-tab-pkg');
    const panelList = document.getElementById('de-panel-list');
    const panelSkillPkg = document.getElementById('de-panel-skill-pkg');
    const panelPkg = document.getElementById('de-panel-pkg');
    const skillListEl = document.getElementById('de-skill-list');
    const refreshBtn = document.getElementById('de-refresh-btn');
    const skillDropzone = document.getElementById('de-skill-dropzone');
    const skillPkgInput = document.getElementById('de-skill-pkg-file-input');
    const skillPkgBrowseBtn = document.getElementById('de-skill-pkg-browse-btn');
    const skillPkgFilename = document.getElementById('de-skill-pkg-filename');
    const skillPkgUploadBtn = document.getElementById('de-skill-pkg-upload-btn');
    const skillPkgResult = document.getElementById('de-skill-pkg-result');
    const pkgDropzone = document.getElementById('de-pkg-dropzone');
    const pkgFileInput = document.getElementById('de-pkg-file-input');
    const pkgBrowseBtn = document.getElementById('de-pkg-browse-btn');
    const pkgFilename = document.getElementById('de-pkg-filename');
    const pkgUploadBtn = document.getElementById('de-pkg-upload-btn');
    const pkgResult = document.getElementById('de-pkg-result');

    if (!overlay) return;

    let skillPkgFile = null, pkgFile = null;

    function esc(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
    function showStatus(msg, isErr) {
        statusBar.textContent = msg; statusBar.className = 'panel-status ' + (isErr ? 'err' : 'ok');
        statusBar.hidden = false;
    }

    function switchTab(tab) {
        tabList.classList.toggle('active', tab === 'list');
        tabSkillPkg.classList.toggle('active', tab === 'skill-pkg');
        tabPkg.classList.toggle('active', tab === 'pkg');
        panelList.hidden = tab !== 'list';
        panelSkillPkg.hidden = tab !== 'skill-pkg';
        panelPkg.hidden = tab !== 'pkg';
        statusBar.hidden = true;
    }

    async function loadSkills() {
        skillListEl.innerHTML = '<div class="panel-hint">加载中…</div>';
        try {
            const headers = await getAuthHeaders();
            const resp = await fetch(getBasePath() + '/admin/skills', { headers });
            if (!resp.ok) { skillListEl.innerHTML = '<div class="panel-hint">加载失败 (HTTP ' + resp.status + ')</div>'; return; }
            const data = await resp.json();
            renderSkillList(data.skills || []);
        } catch (e) { skillListEl.innerHTML = '<div class="panel-hint">加载失败: ' + esc(e.message) + '</div>'; }
    }

    function renderSkillList(skills) {
        if (!skills.length) { skillListEl.innerHTML = '<div class="panel-hint">暂无已安装的技能</div>'; return; }
        skillListEl.innerHTML = '';
        for (const s of skills) {
            const card = document.createElement('div');
            card.className = 'de-skill-card';
            card.innerHTML = `<div class="de-skill-emoji">${s.emoji || '🔧'}</div>
                <div class="de-skill-info"><div class="de-skill-name">${esc(s.name)}</div>${s.description ? `<div class="de-skill-desc">${esc(s.description)}</div>` : ''}</div>
                <span class="de-skill-badge ${s.source || 'builtin'}">${s.source === 'workspace' ? '用户安装' : s.source === 'plugin' ? '插件' : '内置'}</span>`;
            if (s.isUserInstalled) {
                const delBtn = document.createElement('button');
                delBtn.className = 'panel-icon-btn danger';
                delBtn.title = '删除';
                delBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4h6v2"/></svg>';
                delBtn.addEventListener('click', async () => {
                    if (!confirm('确定删除技能 "' + s.name + '"？')) return;
                    delBtn.disabled = true;
                    try {
                        const h = await getAuthHeaders();
                        const r = await fetch(getBasePath() + '/admin/skills/' + encodeURIComponent(s.name), { method: 'DELETE', headers: h });
                        const d = await r.json().catch(() => null);
                        if (r.ok && d?.success) { showStatus('已删除', false); await loadSkills(); }
                        else { showStatus('删除失败: ' + (d?.error || r.status), true); delBtn.disabled = false; }
                    } catch (e) { showStatus('删除失败: ' + e.message, true); delBtn.disabled = false; }
                });
                card.appendChild(delBtn);
            }
            skillListEl.appendChild(card);
        }
    }

    function setSkillPkgFile(file) {
        skillPkgFile = file;
        skillPkgFilename.textContent = file.name + ' (' + (file.size / 1024).toFixed(1) + ' KB)';
        skillPkgFilename.hidden = false;
        skillPkgUploadBtn.disabled = false;
        skillPkgResult.hidden = true;
    }

    function setPkgFile(file) {
        pkgFile = file;
        pkgFilename.textContent = file.name + ' (' + (file.size / 1024).toFixed(1) + ' KB)';
        pkgFilename.hidden = false;
        pkgUploadBtn.disabled = false;
        pkgResult.hidden = true;
    }

    if (skillPkgBrowseBtn) skillPkgBrowseBtn.addEventListener('click', () => skillPkgInput.click());
    if (skillPkgInput) skillPkgInput.addEventListener('change', () => { if (skillPkgInput.files[0]) setSkillPkgFile(skillPkgInput.files[0]); });
    if (skillDropzone) {
        skillDropzone.addEventListener('dragover', e => { e.preventDefault(); skillDropzone.classList.add('drag-over'); });
        skillDropzone.addEventListener('dragleave', () => skillDropzone.classList.remove('drag-over'));
        skillDropzone.addEventListener('drop', e => { e.preventDefault(); skillDropzone.classList.remove('drag-over'); if (e.dataTransfer.files[0]) setSkillPkgFile(e.dataTransfer.files[0]); });
    }
    if (pkgBrowseBtn) pkgBrowseBtn.addEventListener('click', () => pkgFileInput.click());
    if (pkgFileInput) pkgFileInput.addEventListener('change', () => { if (pkgFileInput.files[0]) setPkgFile(pkgFileInput.files[0]); });
    if (pkgDropzone) {
        pkgDropzone.addEventListener('dragover', e => { e.preventDefault(); pkgDropzone.classList.add('drag-over'); });
        pkgDropzone.addEventListener('dragleave', () => pkgDropzone.classList.remove('drag-over'));
        pkgDropzone.addEventListener('drop', e => { e.preventDefault(); pkgDropzone.classList.remove('drag-over'); if (e.dataTransfer.files[0]) setPkgFile(e.dataTransfer.files[0]); });
    }

    if (skillPkgUploadBtn) skillPkgUploadBtn.addEventListener('click', async () => {
        if (!skillPkgFile) return;
        skillPkgUploadBtn.disabled = true; skillPkgUploadBtn.textContent = '上传中…';
        skillPkgResult.hidden = true;
        try {
            const h = await getAuthHeaders(); const fd = new FormData(); fd.append('file', skillPkgFile);
            const resp = await fetch(getBasePath() + '/admin/skills/upload', { method: 'POST', headers: h, body: fd });
            const data = await resp.json().catch(() => null);
            if (resp.ok && data?.success) {
                skillPkgResult.innerHTML = '<strong>技能安装成功</strong><br>当前技能总数：' + (data.totalLoaded ?? 0);
                skillPkgResult.className = 'panel-result ok'; skillPkgResult.hidden = false;
                showStatus('✅ 技能安装成功', false);
            } else {
                skillPkgResult.textContent = '安装失败: ' + (data?.error || resp.status);
                skillPkgResult.className = 'panel-result err'; skillPkgResult.hidden = false;
                showStatus('❌ 安装失败', true);
            }
        } catch (e) {
            skillPkgResult.textContent = '上传失败: ' + e.message;
            skillPkgResult.className = 'panel-result err'; skillPkgResult.hidden = false;
            showStatus('❌ 上传失败', true);
        }
        skillPkgUploadBtn.disabled = false; skillPkgUploadBtn.textContent = '上传安装';
    });

    if (pkgUploadBtn) pkgUploadBtn.addEventListener('click', async () => {
        if (!pkgFile) return;
        pkgUploadBtn.disabled = true; pkgUploadBtn.textContent = '上传中…';
        pkgResult.hidden = true;
        try {
            const h = await getAuthHeaders(); const fd = new FormData(); fd.append('file', pkgFile);
            const resp = await fetch(getBasePath() + '/admin/digital-employee/upload', { method: 'POST', headers: h, body: fd });
            const data = await resp.json().catch(() => null);
            if (resp.ok && (data?.success !== false)) {
                pkgResult.innerHTML = '<strong>数字员工包安装成功</strong>';
                pkgResult.className = 'panel-result ok'; pkgResult.hidden = false;
                showStatus('✅ 安装成功', false);
            } else {
                pkgResult.textContent = '安装失败: ' + (data?.error || resp.status);
                pkgResult.className = 'panel-result err'; pkgResult.hidden = false;
                showStatus('❌ 安装失败', true);
            }
        } catch (e) {
            pkgResult.textContent = '上传失败: ' + e.message;
            pkgResult.className = 'panel-result err'; pkgResult.hidden = false;
            showStatus('❌ 上传失败', true);
        }
        pkgUploadBtn.disabled = false; pkgUploadBtn.textContent = '上传安装';
    });

    if (tabList) tabList.addEventListener('click', () => switchTab('list'));
    if (tabSkillPkg) tabSkillPkg.addEventListener('click', () => switchTab('skill-pkg'));
    if (tabPkg) tabPkg.addEventListener('click', () => switchTab('pkg'));
    if (refreshBtn) refreshBtn.addEventListener('click', loadSkills);
    openBtn.addEventListener('click', async () => { overlay.hidden = false; switchTab('list'); await loadSkills(); });
    closeBtn.addEventListener('click', () => { overlay.hidden = true; });
    overlay.addEventListener('click', e => { if (e.target === overlay) overlay.hidden = true; });
})();

// ============================================================
// WORKSPACE FILES PANEL
// ============================================================
(() => {
    const overlay       = document.getElementById('wf-overlay');
    const openBtn       = document.getElementById('wf-panel-btn');
    const closeBtn      = document.getElementById('wf-close-btn');
    const statusBar     = document.getElementById('wf-panel-status');

    // Tabs
    const tabBrowse    = document.getElementById('wf-tab-browse');
    const tabUpload    = document.getElementById('wf-tab-upload');
    const tabDownload  = document.getElementById('wf-tab-download');
    const panelBrowse  = document.getElementById('wf-panel-browse');
    const panelUpload  = document.getElementById('wf-panel-upload');
    const panelDownload = document.getElementById('wf-panel-download');

    // Browse tab (tree display)
    const browsePathInput  = document.getElementById('wf-browse-path');
    const browseDepthInput = document.getElementById('wf-browse-depth');
    const browseBtn        = document.getElementById('wf-browse-btn');
    const browseResult     = document.getElementById('wf-browse-result');

    // Download tab
    const downloadPathInput = document.getElementById('wf-download-path');
    const downloadBtn       = document.getElementById('wf-download-btn');
    const downloadResult    = document.getElementById('wf-download-result');

    // Upload tab
    const uploadDirInput  = document.getElementById('wf-upload-path');
    const uploadDropzone  = document.getElementById('wf-upload-dropzone');
    const uploadFileInput = document.getElementById('wf-upload-file-input');
    const uploadBrowseBtn = document.getElementById('wf-upload-browse-btn');
    const uploadFilename  = document.getElementById('wf-upload-filename');
    const uploadBtn       = document.getElementById('wf-upload-btn');
    const uploadResult    = document.getElementById('wf-upload-result');

    if (!overlay) return;

    let uploadFiles = [];

    function showStatus(msg, isErr) {
        statusBar.textContent = msg; statusBar.className = 'panel-status ' + (isErr ? 'err' : 'ok');
        statusBar.hidden = false;
        clearTimeout(showStatus._t);
        if (!isErr) showStatus._t = setTimeout(() => { statusBar.hidden = true; }, 3000);
    }

    function switchWfTab(tab) {
        if (tabBrowse)   tabBrowse.classList.toggle('active',   tab === 'browse');
        if (tabUpload)   tabUpload.classList.toggle('active',   tab === 'upload');
        if (tabDownload) tabDownload.classList.toggle('active', tab === 'download');
        if (panelBrowse)   panelBrowse.hidden   = tab !== 'browse';
        if (panelUpload)   panelUpload.hidden   = tab !== 'upload';
        if (panelDownload) panelDownload.hidden = tab !== 'download';
        statusBar.hidden = true;
    }

    // ── Tree helpers ──────────────────────────────────────────────────────
    function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }

    function formatSize(bytes) {
        if (bytes == null || bytes < 0) return '';
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
        return (bytes / 1048576).toFixed(1) + ' MB';
    }

    function renderTree(entries, indent) {
        if (!entries || !entries.length) return '';
        return entries.map(entry => {
            const pad  = '\u00a0'.repeat(indent * 4);
            const icon = entry.isDir
                ? '<span style="color:#e6a800">\uD83D\uDCC1</span>'
                : '<span style="color:#888">\uD83D\uDCC4</span>';
            const size = (!entry.isDir && entry.size != null)
                ? ' <span style="color:var(--text-muted);font-size:11px">(' + formatSize(entry.size) + ')</span>'
                : '';
            const line = `<div style="white-space:nowrap">${pad}${icon} ${escHtml(entry.name)}${size}</div>`;
            const children = (entry.isDir && entry.children) ? renderTree(entry.children, indent + 1) : '';
            return line + children;
        }).join('');
    }

    function countEntries(entries) {
        let dirs = 0, files = 0;
        for (const e of (entries || [])) {
            if (e.isDir) { dirs++; if (e.children) { const c = countEntries(e.children); dirs += c.dirs; files += c.files; } }
            else files++;
        }
        return { dirs, files };
    }

    // ── Browse (tree) ─────────────────────────────────────────────────────
    async function doBrowse() {
        const path  = browsePathInput ? browsePathInput.value.trim() : '';
        const depth = browseDepthInput ? parseInt(browseDepthInput.value, 10) : 6;
        if (browseBtn) browseBtn.disabled = true;
        if (browseResult) browseResult.hidden = true;
        try {
            const headers = await getAuthHeaders();
            const params = new URLSearchParams();
            if (path) params.set('path', path);
            if (!isNaN(depth)) params.set('depth', String(depth));
            const url = getBasePath() + '/admin/workspace/tree' + ([...params].length ? '?' + params : '');
            const resp = await fetch(url, { headers });
            const data = await resp.json().catch(() => null);
            if (resp.ok && data && data.success !== false) {
                const rootLabel = data.root || '（工作区根目录）';
                const treeHtml  = renderTree(data.entries || [], 0);
                if (browseResult) {
                    browseResult.innerHTML = '<div style="color:var(--success,#2e7d32);margin-bottom:6px">\uD83D\uDCC2 ' + escHtml(rootLabel) + '</div>'
                        + (treeHtml || '<div style="opacity:0.5">（空目录）</div>');
                    browseResult.hidden = false;
                }
                const cnt = countEntries(data.entries || []);
                showStatus('✅ ' + cnt.dirs + ' 个目录，' + cnt.files + ' 个文件', false);
            } else {
                // Fallback: try /admin/workspace/browse (flat list)
                const resp2 = await fetch(getBasePath() + '/admin/workspace/browse' + (path ? '?path=' + encodeURIComponent(path) : ''), { headers });
                if (!resp2.ok) { showStatus('查询失败 (' + resp2.status + ')', true); return; }
                const data2 = await resp2.json();
                const files = data2.files || data2.items || [];
                const treeHtml2 = files.map(f => {
                    const icon = f.isDirectory ? '\uD83D\uDCC1' : '\uD83D\uDCC4';
                    const sz   = (!f.isDirectory && f.size != null) ? ' <span style="color:var(--text-muted);font-size:11px">(' + formatSize(f.size) + ')</span>' : '';
                    return `<div style="white-space:nowrap">${icon} ${escHtml(f.name || f.path || '')}${sz}</div>`;
                }).join('');
                if (browseResult) {
                    browseResult.innerHTML = treeHtml2 || '<div style="opacity:0.5">（空目录）</div>';
                    browseResult.hidden = false;
                }
                showStatus('✅ 共 ' + files.length + ' 项', false);
            }
        } catch (e) {
            if (browseResult) {
                browseResult.innerHTML = '<span style="color:var(--danger,#c00)">❌ ' + escHtml(e.message) + '</span>';
                browseResult.hidden = false;
            }
            showStatus('❌ 查询失败：' + e.message, true);
        } finally {
            if (browseBtn) browseBtn.disabled = false;
        }
    }

    if (browseBtn) browseBtn.addEventListener('click', doBrowse);
    if (browsePathInput) browsePathInput.addEventListener('keydown', e => { if (e.key === 'Enter') void doBrowse(); });

    // ── Download ──────────────────────────────────────────────────────────
    if (downloadBtn) downloadBtn.addEventListener('click', async () => {
        const path = downloadPathInput ? downloadPathInput.value.trim() : '';
        downloadBtn.disabled = true;
        if (downloadResult) downloadResult.hidden = true;
        const url = getBasePath() + '/admin/workspace/download' + (path ? '?path=' + encodeURIComponent(path) : '');
        try {
            const headers = await getAuthHeaders();
            const resp = await fetch(url, { headers });
            if (!resp.ok) {
                const data = await resp.json().catch(() => null);
                const msg = (data && data.error) || 'HTTP ' + resp.status;
                downloadResult.innerHTML = '<strong>❌ 下载失败</strong><br>' + escHtml(msg);
                downloadResult.className = 'panel-result err'; downloadResult.hidden = false;
                showStatus('❌ 下载失败：' + msg, true);
                return;
            }
            const cd = resp.headers.get('Content-Disposition') || '';
            let filename = path ? path.replace(/.*[\\/]/, '') || 'download' : 'workspace.zip';
            const cdMatch = cd.match(/filename[^;=\n]*=["']?([^"';\n]*)["']?/);
            if (cdMatch && cdMatch[1].trim()) filename = cdMatch[1].trim();
            const blob = await resp.blob();
            const objUrl = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = objUrl; a.download = filename;
            document.body.appendChild(a); a.click(); a.remove();
            setTimeout(() => URL.revokeObjectURL(objUrl), 10000);
            downloadResult.innerHTML = '✅ 已触发下载：<code>' + escHtml(filename) + '</code>（' + formatSize(blob.size) + '）';
            downloadResult.className = 'panel-result ok'; downloadResult.hidden = false;
            showStatus('✅ 下载完成：' + filename, false);
        } catch (e) {
            downloadResult.innerHTML = '<strong>❌ 网络错误</strong><br>' + escHtml(e.message);
            downloadResult.className = 'panel-result err'; downloadResult.hidden = false;
            showStatus('❌ 网络错误：' + e.message, true);
        } finally {
            downloadBtn.disabled = false;
        }
    });

    // ── Upload ────────────────────────────────────────────────────────────
    function setUploadFiles(files) {
        uploadFiles = Array.from(files);
        if (uploadFilename) {
            uploadFilename.textContent = uploadFiles.map(f => f.name).join(', ');
            uploadFilename.hidden = !uploadFiles.length;
        }
        if (uploadBtn) uploadBtn.disabled = !uploadFiles.length;
        if (uploadResult) uploadResult.hidden = true;
    }

    if (uploadBrowseBtn) uploadBrowseBtn.addEventListener('click', () => uploadFileInput && uploadFileInput.click());
    if (uploadFileInput) uploadFileInput.addEventListener('change', () => setUploadFiles(uploadFileInput.files || []));
    if (uploadDropzone) {
        uploadDropzone.addEventListener('dragover', e => { e.preventDefault(); uploadDropzone.classList.add('drag-over'); });
        uploadDropzone.addEventListener('dragleave', () => uploadDropzone.classList.remove('drag-over'));
        uploadDropzone.addEventListener('drop', e => { e.preventDefault(); uploadDropzone.classList.remove('drag-over'); setUploadFiles(e.dataTransfer.files); });
    }

    if (uploadBtn) uploadBtn.addEventListener('click', async () => {
        if (!uploadFiles.length) return;
        uploadBtn.disabled = true; uploadBtn.textContent = '上传中…';
        uploadResult.hidden = true;
        const dir = uploadDirInput ? uploadDirInput.value.trim() : '';
        const url = getBasePath() + '/admin/workspace/upload' + (dir ? '?dir=' + encodeURIComponent(dir) : '');
        try {
            const headers = await getAuthHeaders();
            const fd = new FormData();
            uploadFiles.forEach(f => fd.append('files', f, f.name));
            const resp = await fetch(url, { method: 'POST', headers, body: fd });
            const data = await resp.json().catch(() => null);
            if (resp.ok && data && data.success !== false) {
                const paths = (data.files || []).map(p => escHtml(p));
                uploadResult.innerHTML = '<strong>✅ 上传成功</strong>'
                    + (paths.length ? '<ul style="margin:6px 0 0 0;padding-left:18px">' + paths.map(p => `<li>${p}</li>`).join('') + '</ul>' : '');
                uploadResult.className = 'panel-result ok'; uploadResult.hidden = false;
                showStatus('✅ 上传成功，写入 ' + (data.files || uploadFiles).length + ' 个文件', false);
                setUploadFiles([]);
                if (uploadFileInput) uploadFileInput.value = '';
            } else {
                const msg = (data && data.error) || 'HTTP ' + resp.status;
                uploadResult.innerHTML = '<strong>❌ 上传失败</strong><br>' + escHtml(msg);
                uploadResult.className = 'panel-result err'; uploadResult.hidden = false;
                showStatus('❌ 上传失败：' + msg, true);
                uploadBtn.disabled = false;
            }
        } catch (e) {
            uploadResult.innerHTML = '<strong>❌ 网络错误</strong><br>' + escHtml(e.message);
            uploadResult.className = 'panel-result err'; uploadResult.hidden = false;
            showStatus('❌ 网络错误：' + e.message, true);
            uploadBtn.disabled = false;
        } finally {
            if (uploadBtn) uploadBtn.textContent = '上传';
        }
    });

    // ── Panel open/close ──────────────────────────────────────────────────
    if (tabBrowse)   tabBrowse.addEventListener('click',   () => switchWfTab('browse'));
    if (tabUpload)   tabUpload.addEventListener('click',   () => switchWfTab('upload'));
    if (tabDownload) tabDownload.addEventListener('click', () => switchWfTab('download'));

    openBtn.addEventListener('click', () => { overlay.hidden = false; switchWfTab('browse'); void doBrowse(); });
    closeBtn.addEventListener('click', () => { overlay.hidden = true; });
    overlay.addEventListener('click', e => { if (e.target === overlay) overlay.hidden = true; });
})();
// ============================================================
// CRON / AUTOMATION PANEL
// ============================================================
(() => {
    const overlay = document.getElementById('cron-overlay');
    const openBtn = document.getElementById('cron-panel-btn');
    const closeBtn = document.getElementById('cron-close-btn');
    const refreshBtn = document.getElementById('cron-refresh-btn');
    const addBtn = document.getElementById('cron-add-btn');
    const jobList = document.getElementById('cron-job-list');
    const formSection = document.getElementById('cron-form-section');
    const formTitle = document.getElementById('cron-form-title');
    const formError = document.getElementById('cron-form-error');
    const cancelBtn = document.getElementById('cron-form-cancel-btn');
    const saveBtn = document.getElementById('cron-form-save-btn');
    const statusBar = document.getElementById('cron-panel-status');
    const histSection = document.getElementById('cron-history-section');
    const histTitle = document.getElementById('cron-history-title');
    const histList = document.getElementById('cron-history-list');
    const histCloseBtn = document.getElementById('cron-history-close-btn');
    const sessViewBtn = document.getElementById('cron-session-view-btn');
    const sessSection = document.getElementById('cron-session-section');
    const sessTitle = document.getElementById('cron-session-title');
    const sessList = document.getElementById('cron-session-list');
    const sessCloseBtn = document.getElementById('cron-session-close-btn');
    const fName = document.getElementById('cron-f-name');
    const fPrompt = document.getElementById('cron-f-prompt');
    const fTimezone = document.getElementById('cron-f-timezone');
    const fModel = document.getElementById('cron-f-model');
    const fChannel = document.getElementById('cron-f-channel');
    const fRecipient = document.getElementById('cron-f-recipient');
    const fEnabled = document.getElementById('cron-f-enabled');
    const fScheduleRaw = document.getElementById('cron-f-schedule-raw');
    const dailyTime = document.getElementById('cron-daily-time');
    const dailyWeekday = document.getElementById('cron-daily-weekday');
    const intervalVal = document.getElementById('cron-interval-val');
    const intervalUnit = document.getElementById('cron-interval-unit');

    if (!overlay) return;

    let editingId = null;
    let currentHistJob = null;
    let currentPreset = 'daily';

    function showStatus(msg, isErr) {
        statusBar.textContent = msg; statusBar.className = 'panel-status ' + (isErr ? 'err' : 'ok');
        statusBar.hidden = false;
        clearTimeout(showStatus._t);
        if (!isErr) showStatus._t = setTimeout(() => { statusBar.hidden = true; }, 3500);
    }

    document.querySelectorAll('.cron-freq-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('.cron-freq-tab').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            currentPreset = tab.dataset.preset;
            document.getElementById('cron-preset-daily').style.display = currentPreset === 'daily' ? '' : 'none';
            document.getElementById('cron-preset-interval').style.display = currentPreset === 'interval' ? 'flex' : 'none';
            document.getElementById('cron-preset-custom').style.display = currentPreset === 'custom' ? '' : 'none';
        });
    });

    function buildSchedule() {
        if (currentPreset === 'custom') return fScheduleRaw.value.trim();
        if (currentPreset === 'daily') {
            const parts = (dailyTime.value || '09:00').split(':');
            const h = parseInt(parts[0], 10) || 9, m = parseInt(parts[1], 10) || 0;
            return `${m} ${h} * * ${dailyWeekday.checked ? '1-5' : '*'}`;
        }
        if (currentPreset === 'interval') {
            const v = parseInt(intervalVal.value, 10) || 1;
            return intervalUnit.value === 'min' ? `*/${v} * * * *` : `0 */${v} * * *`;
        }
        return '';
    }

    function applyScheduleToPreset(schedule) {
        if (!schedule) return;
        const daily = schedule.match(/^(\d+)\s+(\d+)\s+\*\s+\*\s+([\d\-,]+|\*)$/);
        if (daily) {
            const h = parseInt(daily[2], 10), m = parseInt(daily[1], 10);
            dailyTime.value = `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}`;
            dailyWeekday.checked = daily[3] === '1-5';
            setPreset('daily'); return;
        }
        const minM = schedule.match(/^\*\/(\d+)\s+\*\s+\*\s+\*\s+\*$/);
        if (minM) { intervalVal.value = minM[1]; intervalUnit.value = 'min'; setPreset('interval'); return; }
        const hrM = schedule.match(/^0\s+\*\/(\d+)\s+\*\s+\*\s+\*$/);
        if (hrM) { intervalVal.value = hrM[1]; intervalUnit.value = 'hour'; setPreset('interval'); return; }
        fScheduleRaw.value = schedule; setPreset('custom');
    }

    function setPreset(preset) {
        currentPreset = preset;
        document.querySelectorAll('.cron-freq-tab').forEach(t => t.classList.toggle('active', t.dataset.preset === preset));
        document.getElementById('cron-preset-daily').style.display = preset === 'daily' ? '' : 'none';
        document.getElementById('cron-preset-interval').style.display = preset === 'interval' ? 'flex' : 'none';
        document.getElementById('cron-preset-custom').style.display = preset === 'custom' ? '' : 'none';
    }

    function relTime(iso) {
        if (!iso) return '—';
        const sec = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
        if (sec < 60) return '刚刚';
        if (sec < 3600) return Math.floor(sec/60) + ' 分钟前';
        if (sec < 86400) return Math.floor(sec/3600) + ' 小时前';
        return new Date(iso).toLocaleString('zh-CN');
    }

    function srcLabel(s) { return s === 'legacy-cron' ? '内置' : s === 'agent' ? 'Agent' : 'Web'; }
    function srcCls(s) { return s === 'legacy-cron' ? 'source-legacy' : s === 'agent' ? 'source-agent' : 'source-webchat'; }

    function buildCard(job) {
        const isStatic = job.source === 'legacy-cron';
        const card = document.createElement('div');
        card.className = 'cron-job-card' + (job.enabled === false ? ' disabled' : '');

        const dot = document.createElement('div'); dot.className = 'cron-job-status';
        const info = document.createElement('div'); info.className = 'cron-job-info';
        const nameEl = document.createElement('div'); nameEl.className = 'cron-job-name'; nameEl.textContent = job.name || job.id;
        const meta = document.createElement('div'); meta.className = 'cron-job-meta'; meta.textContent = job.schedule || '';
        info.appendChild(nameEl); info.appendChild(meta);

        const badge = document.createElement('span');
        badge.className = `cron-job-badge ${srcCls(job.source)}`; badge.textContent = srcLabel(job.source);

        const actions = document.createElement('div'); actions.className = 'cron-job-actions';

        const runBtn = document.createElement('button');
        runBtn.className = 'panel-icon-btn'; runBtn.title = '立即执行';
        runBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="5 3 19 12 5 21 5 3"/></svg>';
        runBtn.addEventListener('click', () => void runJob(job.id));

        const histBtn = document.createElement('button');
        histBtn.className = 'panel-icon-btn'; histBtn.title = '执行历史';
        histBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>';
        histBtn.addEventListener('click', () => void loadHistory(job));

        actions.append(runBtn, histBtn);

        if (!isStatic) {
            const editBtn = document.createElement('button');
            editBtn.className = 'panel-icon-btn'; editBtn.title = '编辑';
            editBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></svg>';
            editBtn.addEventListener('click', () => openForm(job));

            const delBtn = document.createElement('button');
            delBtn.className = 'panel-icon-btn danger'; delBtn.title = '删除';
            delBtn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4h6v2"/></svg>';
            delBtn.addEventListener('click', () => void deleteJob(job.id, job.name || job.id));
            actions.append(editBtn, delBtn);
        }

        card.append(dot, info, badge, actions);
        return card;
    }

    function renderJobs(items) {
        jobList.innerHTML = '';
        if (!items?.length) { const e = document.createElement('div'); e.className = 'panel-hint'; e.textContent = '暂无定时任务'; jobList.appendChild(e); return; }
        const sorted = [...items].sort((a, b) => {
            const as = a.source === 'legacy-cron' ? 0 : 1, bs = b.source === 'legacy-cron' ? 0 : 1;
            return as - bs || (a.name || a.id).localeCompare(b.name || b.id, 'zh-CN');
        });
        sorted.forEach(j => jobList.appendChild(buildCard(j)));
    }

    async function loadJobs() {
        try {
            const h = await getAuthHeaders();
            const r = await fetch(getBasePath() + '/admin/automations', { headers: h });
            if (r.ok) { const d = await r.json(); renderJobs(d.items || []); }
            else showStatus('加载失败 (' + r.status + ')', true);
        } catch (e) { showStatus('加载失败: ' + e.message, true); }
    }

    async function loadHistory(job) {
        currentHistJob = job;
        histTitle.textContent = (job.name || job.id) + ' · 执行历史';
        histList.innerHTML = '<div class="panel-hint">加载中…</div>';
        formSection.hidden = true; sessSection.hidden = true; histSection.hidden = false;
        sessViewBtn.hidden = false;
        try {
            const h = await getAuthHeaders();
            const r = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(job.id), { headers: h });
            if (!r.ok) { histList.innerHTML = '<div class="panel-hint">加载失败 (' + r.status + ')</div>'; return; }
            const d = await r.json();
            renderHistory(d.runState);
        } catch (e) { histList.innerHTML = '<div class="panel-hint">加载失败: ' + e.message + '</div>'; }
    }

    function renderHistory(runState) {
        histList.innerHTML = '';
        if (!runState) { histList.innerHTML = '<div class="panel-hint">无历史记录</div>'; return; }
        if (runState.lastRunAtUtc) {
            const s = document.createElement('div');
            s.className = 'panel-hint';
            const o = runState.outcome === 'success' ? '✅ 成功' : runState.outcome === 'failure' ? '❌ 失败' : runState.outcome || '—';
            s.textContent = '上次执行: ' + relTime(runState.lastRunAtUtc) + ' · ' + o;
            histList.appendChild(s);
        }
        const runs = runState.recentRuns || [];
        if (!runs.length) { const e = document.createElement('div'); e.className = 'panel-hint'; e.textContent = '暂无详细记录'; histList.appendChild(e); return; }
        for (const run of runs) {
            const row = document.createElement('div'); row.className = 'cron-history-row';
            const dotEl = document.createElement('div'); dotEl.className = 'cron-history-dot ' + (run.outcome === 'success' ? 'ok' : run.outcome === 'failure' ? 'error' : '');
            const meta = document.createElement('div'); meta.className = 'cron-history-meta';
            const tk = (run.inputTokens || run.outputTokens) ? ' · ' + ((run.inputTokens||0)+(run.outputTokens||0)) + ' tokens' : '';
            meta.textContent = relTime(run.ranAtUtc) + tk;
            const prev = document.createElement('div'); prev.className = 'cron-history-preview'; prev.textContent = run.messagePreview || '—';
            const detail = document.createElement('div'); detail.className = 'cron-history-detail'; detail.hidden = true;
            const o = run.outcome === 'success' ? '✅ 成功' : run.outcome === 'failure' ? '❌ 失败' : run.outcome || '—';
            detail.innerHTML = `<div class="cron-history-detail-row"><span>执行时间</span><span>${run.ranAtUtc ? new Date(run.ranAtUtc).toLocaleString() : '—'}</span></div>
                <div class="cron-history-detail-row"><span>结果</span><span>${o}</span></div>
                <div class="cron-history-detail-row"><span>输入 tokens</span><span>${run.inputTokens ?? 0}</span></div>
                <div class="cron-history-detail-row"><span>输出 tokens</span><span>${run.outputTokens ?? 0}</span></div>
                ${run.messagePreview ? `<div class="cron-history-detail-preview">${run.messagePreview.replace(/</g,'&lt;')}</div>` : ''}`;
            row.addEventListener('click', () => { detail.hidden = !detail.hidden; row.classList.toggle('cron-history-row-open', !detail.hidden); });
            row.append(dotEl, meta, prev);
            histList.appendChild(row); histList.appendChild(detail);
        }
    }

    function openForm(job) {
        editingId = job ? job.id : null;
        formTitle.textContent = job ? '编辑定时任务' : '新建定时任务';
        formError.hidden = true; histSection.hidden = true;
        if (job) {
            fName.value = job.name || ''; fPrompt.value = job.prompt || '';
            fTimezone.value = job.timezone || ''; fModel.value = job.modelId || '';
            fChannel.value = job.deliveryChannelId || ''; fRecipient.value = job.deliveryRecipientId || '';
            fEnabled.checked = job.enabled !== false;
            applyScheduleToPreset(job.schedule || '');
        } else {
            fName.value = ''; fPrompt.value = ''; fTimezone.value = 'Asia/Shanghai'; fModel.value = '';
            fChannel.value = ''; fRecipient.value = ''; fEnabled.checked = true;
            dailyTime.value = '09:00'; dailyWeekday.checked = false;
            intervalVal.value = '1'; intervalUnit.value = 'hour'; fScheduleRaw.value = '';
            setPreset('daily');
        }
        formSection.hidden = false; addBtn.hidden = true;
        fName.focus();
    }

    function closeForm() { formSection.hidden = true; histSection.hidden = true; addBtn.hidden = false; editingId = null; }

    async function saveJob() {
        const name = fName.value.trim(), schedule = buildSchedule(), prompt = fPrompt.value.trim();
        if (!name) { formError.textContent = '请填写任务名称'; formError.hidden = false; return; }
        if (!schedule) { formError.textContent = '请设置执行计划'; formError.hidden = false; return; }
        if (!prompt) { formError.textContent = '请填写提示词'; formError.hidden = false; return; }
        formError.hidden = true;
        const payload = { id: editingId || '', name, schedule, prompt,
            timezone: fTimezone.value.trim() || null, modelId: fModel.value.trim() || null,
            deliveryChannelId: fChannel.value.trim() || null, deliveryRecipientId: fRecipient.value.trim() || null,
            enabled: fEnabled.checked, source: editingId ? undefined : 'webchat' };
        try {
            const h = { 'Content-Type': 'application/json', ...(await getAuthHeaders()) };
            const url = getBasePath() + '/admin/automations' + (editingId ? '/' + encodeURIComponent(editingId) : '');
            const resp = await fetch(url, { method: editingId ? 'PUT' : 'POST', headers: h, body: JSON.stringify(payload) });
            if (!resp.ok) {
                const e = await resp.json().catch(() => ({ error: '保存失败 (' + resp.status + ')' }));
                formError.textContent = e.error || '保存失败 (' + resp.status + ')'; formError.hidden = false; return;
            }
            closeForm(); showStatus('保存成功！', false); await loadJobs();
        } catch (e) { formError.textContent = '保存失败: ' + e.message; formError.hidden = false; }
    }

    async function deleteJob(id, name) {
        if (!confirm(`确定删除 "${name}"？`)) return;
        try {
            const h = await getAuthHeaders();
            const r = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(id), { method: 'DELETE', headers: h });
            if (r.ok) { showStatus('已删除', false); await loadJobs(); }
            else { const e = await r.json().catch(() => ({ error: '删除失败' })); showStatus(e.error || '删除失败', true); }
        } catch (e) { showStatus('删除失败: ' + e.message, true); }
    }

    async function runJob(id) {
        try {
            const h = { 'Content-Type': 'application/json', ...(await getAuthHeaders()) };
            const r = await fetch(getBasePath() + '/admin/automations/' + encodeURIComponent(id) + '/run', { method: 'POST', headers: h });
            if (r.ok) showStatus('已触发执行！', false);
            else { const e = await r.json().catch(() => ({ error: '执行失败' })); showStatus(e.error || '执行失败', true); }
        } catch (e) { showStatus('执行失败: ' + e.message, true); }
    }

    async function loadSession() {
        if (!currentHistJob) return;
        const sessionId = currentHistJob.sessionId || ('automation:' + currentHistJob.id);
        sessTitle.textContent = (currentHistJob.name || currentHistJob.id) + ' · 完整会话';
        sessList.innerHTML = '<div class="panel-hint">加载中…</div>';
        histSection.hidden = true; sessSection.hidden = false;
        try {
            const h = await getAuthHeaders();
            const r = await fetch(getBasePath() + '/admin/sessions/' + encodeURIComponent(sessionId), { headers: h });
            if (r.status === 404) { sessList.innerHTML = '<div class="panel-hint">暂无会话记录</div>'; return; }
            if (!r.ok) { sessList.innerHTML = '<div class="panel-hint">加载失败 (' + r.status + ')</div>'; return; }
            const d = await r.json();
            renderCronSession(d.session?.history || []);
        } catch (e) { sessList.innerHTML = '<div class="panel-hint">加载失败: ' + e.message + '</div>'; }
    }

    function renderCronSession(history) {
        sessList.innerHTML = '';
        if (!history?.length) { sessList.innerHTML = '<div class="panel-hint">暂无对话记录</div>'; return; }
        for (const turn of history) {
            if (!turn.content && !turn.toolCalls?.length) continue;
            const wrap = document.createElement('div');
            wrap.className = 'cron-sess-turn cron-sess-turn-' + (turn.role === 'user' ? 'user' : 'assistant');
            const hdr = document.createElement('div'); hdr.className = 'cron-sess-turn-header';
            hdr.textContent = (turn.role === 'user' ? '指令' : 'AI 回复') + (turn.timestamp ? '  ' + new Date(turn.timestamp).toLocaleString('zh-CN') : '');
            const body = document.createElement('div'); body.className = 'cron-sess-turn-body'; body.textContent = turn.content || '';
            wrap.appendChild(hdr); wrap.appendChild(body);
            if (turn.toolCalls?.length) {
                for (const tc of turn.toolCalls) {
                    const t = document.createElement('div'); t.className = 'cron-sess-tool-call';
                    t.textContent = '🔧 ' + tc.toolName; t.title = tc.arguments || '';
                    wrap.appendChild(t);
                }
            }
            sessList.appendChild(wrap);
        }
        sessList.scrollTop = sessList.scrollHeight;
    }

    openBtn.addEventListener('click', async () => { overlay.hidden = false; closeForm(); statusBar.hidden = true; await loadJobs(); });
    closeBtn.addEventListener('click', () => { overlay.hidden = true; closeForm(); });
    overlay.addEventListener('click', e => { if (e.target === overlay) { overlay.hidden = true; closeForm(); } });
    refreshBtn.addEventListener('click', loadJobs);
    addBtn.addEventListener('click', () => openForm(null));
    cancelBtn.addEventListener('click', closeForm);
    histCloseBtn.addEventListener('click', closeForm);
    sessViewBtn.addEventListener('click', loadSession);
    sessCloseBtn.addEventListener('click', () => { sessSection.hidden = true; histSection.hidden = false; });
    saveBtn.addEventListener('click', saveJob);
})();
