(function () {
    const PREFIX = 'openclaw_oidc_';

    function randomString(length) {
        const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        const data = new Uint8Array(length);
        crypto.getRandomValues(data);
        let out = '';
        for (let i = 0; i < length; i += 1) {
            out += chars[data[i] % chars.length];
        }
        return out;
    }

    function parseJwtExp(token) {
        if (!token || token.split('.').length < 2) {
            return null;
        }
        try {
            const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
            const decoded = atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='));
            const obj = JSON.parse(decoded);
            return typeof obj.exp === 'number' ? obj.exp : null;
        } catch {
            return null;
        }
    }

    function makeStorage(persistent) {
        const chosen = persistent ? localStorage : sessionStorage;
        return {
            get: (key) => chosen.getItem(PREFIX + key),
            set: (key, value) => chosen.setItem(PREFIX + key, value),
            remove: (key) => chosen.removeItem(PREFIX + key),
            clear: () => {
                Object.keys(localStorage)
                    .filter((k) => k.startsWith(PREFIX))
                    .forEach((k) => localStorage.removeItem(k));
                Object.keys(sessionStorage)
                    .filter((k) => k.startsWith(PREFIX))
                    .forEach((k) => sessionStorage.removeItem(k));
            }
        };
    }

    async function fetchDiscovery(authority) {
        const trimmed = (authority || '').replace(/\/+$/, '');
        const resp = await fetch(trimmed + '/.well-known/openid-configuration', { method: 'GET' });
        if (!resp.ok) {
            throw new Error('OIDC discovery failed: ' + resp.status);
        }
        return resp.json();
    }

    function create(config) {
        const authority = (config.authority || '').replace(/\/+$/, '');
        const clientId = config.clientId || '';
        const scope = config.scope || 'openid profile email';
        const persist = Boolean(config.persist);
        const storage = makeStorage(persist);
        const redirectUri = config.redirectUri || window.location.origin + window.location.pathname;

        if (!authority) {
            throw new Error('OIDC authority is required.');
        }

        let discovery = null;

        async function ensureDiscovery() {
            if (!discovery) {
                discovery = await fetchDiscovery(authority);
            }
            return discovery;
        }

        function saveTokenSet(tokenSet) {
            storage.set('token_set', JSON.stringify(tokenSet));
        }

        function getTokenSet() {
            const raw = storage.get('token_set');
            if (!raw) {
                return null;
            }
            try {
                return JSON.parse(raw);
            } catch {
                return null;
            }
        }

        function getAccessToken() {
            const tokenSet = getTokenSet();
            return tokenSet?.access_token || null;
        }

        async function login(extra) {
            if (!clientId) {
                throw new Error('OIDC clientId is required.');
            }

            const endpoints = await ensureDiscovery();
            const state = randomString(32);

            storage.set('state', state);
            if (extra?.returnTo) {
                storage.set('return_to', extra.returnTo);
            }

            const params = new URLSearchParams({
                client_id: clientId,
                redirect_uri: redirectUri,
                response_type: 'code',
                scope: scope,
                state: state
            });

            window.location.assign(endpoints.authorization_endpoint + '?' + params.toString());
        }

        async function exchangeCode(code) {
            const endpoints = await ensureDiscovery();

            const body = new URLSearchParams({
                grant_type: 'authorization_code',
                client_id: clientId,
                code: code,
                redirect_uri: redirectUri
            });

            const resp = await fetch(endpoints.token_endpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body
            });

            if (!resp.ok) {
                throw new Error('Token exchange failed: ' + resp.status);
            }

            const tokenSet = await resp.json();
            saveTokenSet(tokenSet);
            return tokenSet;
        }

        async function refreshIfNeeded(minValidSeconds = 30) {
            const tokenSet = getTokenSet();
            if (!tokenSet?.access_token) {
                return null;
            }

            const exp = parseJwtExp(tokenSet.access_token);
            const now = Math.floor(Date.now() / 1000);
            if (exp && exp - now > minValidSeconds) {
                return tokenSet.access_token;
            }

            if (!tokenSet.refresh_token) {
                return tokenSet.access_token;
            }

            const endpoints = await ensureDiscovery();
            const body = new URLSearchParams({
                grant_type: 'refresh_token',
                client_id: clientId,
                refresh_token: tokenSet.refresh_token
            });

            const resp = await fetch(endpoints.token_endpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body
            });

            if (!resp.ok) {
                clear();
                return null;
            }

            const next = await resp.json();
            // Keep old refresh token when provider does not rotate it.
            if (!next.refresh_token && tokenSet.refresh_token) {
                next.refresh_token = tokenSet.refresh_token;
            }
            saveTokenSet(next);
            return next.access_token || null;
        }

        async function handleRedirectCallback() {
            const params = new URLSearchParams(window.location.search);
            const code = params.get('code');
            const state = params.get('state');
            const error = params.get('error');
            if (!code && !error) {
                return { handled: false };
            }

            if (error) {
                return { handled: true, ok: false, error: error + (params.get('error_description') ? ': ' + params.get('error_description') : '') };
            }

            const expectedState = storage.get('state');
            if (!state || !expectedState || state !== expectedState) {
                return { handled: true, ok: false, error: 'OIDC state mismatch.' };
            }

            storage.remove('state');
            await exchangeCode(code);

            const returnTo = storage.get('return_to');
            storage.remove('return_to');

            const url = new URL(window.location.href);
            url.searchParams.delete('code');
            url.searchParams.delete('state');
            url.searchParams.delete('session_state');
            url.searchParams.delete('iss');
            url.searchParams.delete('error');
            url.searchParams.delete('error_description');
            window.history.replaceState({}, document.title, url.toString());

            return { handled: true, ok: true, returnTo: returnTo || null };
        }

        async function logout() {
            const tokenSet = getTokenSet();
            const endpoints = await ensureDiscovery();
            clear();

            const logoutEndpoint = endpoints.end_session_endpoint || endpoints.revocation_endpoint;
            if (!logoutEndpoint) {
                return;
            }

            const params = new URLSearchParams({
                post_logout_redirect_uri: redirectUri,
                client_id: clientId
            });
            if (tokenSet?.id_token) {
                params.set('id_token_hint', tokenSet.id_token);
            }
            window.location.assign(logoutEndpoint + '?' + params.toString());
        }

        function clear() {
            storage.clear();
        }

        return {
            login,
            logout,
            clear,
            getAccessToken,
            refreshIfNeeded,
            handleRedirectCallback
        };
    }

    window.OpenClawOidc = { create };
})();
