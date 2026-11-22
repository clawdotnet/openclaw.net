# Keycloak Integration Guide (OpenClaw.NET as an OIDC Client)

OpenClaw.NET has built-in OIDC/JWT Bearer support. Connecting Keycloak **requires no code
changes** — only configuration plus the right claims on the Keycloak side. This document
records the verified path and the three pitfalls you will hit.

For the broader authentication architecture, see
[Authentication System Technical Reference](AUTHENTICATION.md).

**Runnable example**: [`deploy/keycloak/`](../deploy/keycloak/) — Keycloak orchestration,
realm import file, three seed users, one-command startup and end-to-end verification.

```bash
cd deploy/keycloak
./scripts/up.sh        # start Keycloak, import realm, patch import gaps
./scripts/verify.sh    # verify issuer / JWKS / claims / roles
```

The rest of this document explains **why** the configuration has to look like this.

---

## 1. Minimal working configuration

```jsonc
{
  "OpenClaw": {
    "Security": {
      "AuthMode": "oidc",
      "AlwaysRequireAuth": true,
      "Oidc": {
        "Authority": "https://kc.example.com/realms/openclaw",
        "Audience": "openclaw",
        "RequireHttpsMetadata": true,
        "RoleClaim": "operator_role"
      }
    }
  }
}
```

Or via environment variables (more convenient for containers):

```bash
OpenClaw__Security__AuthMode=oidc
OpenClaw__Security__AlwaysRequireAuth=true
OpenClaw__Security__Oidc__Authority=https://kc.example.com/realms/openclaw
OpenClaw__Security__Oidc__Audience=openclaw
OpenClaw__Security__Oidc__RequireHttpsMetadata=true
OpenClaw__Security__Oidc__RoleClaim=operator_role
```

Only set `RequireHttpsMetadata` to `false` for local HTTP testing; **production must use `true`**.

---

## 2. What to configure on the Keycloak side

### 2.1 Client

| Field | Value |
|---|---|
| Client type | OpenID Connect |
| Client ID | `openclaw` (or anything) |
| Client authentication | Browser sign-in → Public + PKCE; backend calls → Confidential |
| Standard flow | On |
| Valid redirect URIs | `https://<openclaw-host>/*` |

### 2.2 Three claims that must be present

| Claim | Source | Consequence if missing |
|---|---|---|
| `sub` | Built into Keycloak | `AccountId` is empty; audit entries cannot be attributed |
| `preferred_username` | Built into Keycloak | Display name falls back to `sub` |
| **`operator_role`** | **You must add it** | **Always viewer; the user cannot even chat** |

The first two are there by default. The third is the subject of this document — see below.

### 2.3 Multiple roles: highest role now wins, but a single-valued claim is still preferred

**Fixed**: `EndpointHelpers` used `FindFirstValue(RoleClaim)`, which reads only the
**first** claim. Keycloak's `realm_roles` is a JSON array, and the JWT handler flattens it
into one claim per element:

```
realm_roles claims = [viewer, admin], FindFirstValue = viewer
                                          ^ only viewer is read, admin is lost
```

Because `Normalize` maps unknown values to `viewer`, an admin was silently downgraded to viewer.

Resolution now reads **every** claim and hands them to `OperatorRoleNames.FromRoles`,
which picks the highest rank, independent of order:

```
realm_roles claims = [viewer, admin] -> FromRoles -> admin
realm_roles claims = [admin, viewer] -> FromRoles -> admin   <- order does not matter
```

Four behavioural boundaries (all pinned by `OidcRoleClaimResolutionTests`):

| Input | Result | Why |
|---|---|---|
| `[viewer, admin]` | `admin` | Highest wins |
| `[app_user, some_role]` | `viewer` | Unknown names ignored; authorization must not open up for them |
| `[app_user, admin]` | `admin` | Unknown names must not disturb a known role |
| `[]` / `null` | `viewer` | Fail closed when the claim is absent |

**One thing has not changed**: role values must be exactly `viewer`, `operator`, or
`admin`. A name like `app_admin` is **never guessed** to be `admin` — fuzzy matching would
also promote `app_administrator` or `not_admin`. The mapping to these three values is the
identity provider's responsibility.

**Recommended** (more deterministic than relying on the array): carry the role in a
single-valued user attribute.

Client scope → Mappers → Add:

| Field | Value |
|---|---|
| Mapper type | User Attribute |
| User Attribute | `operator_role` |
| Token Claim Name | `operator_role` |
| Claim JSON Type | String |
| Add to access token | On |

Then `RoleClaim=operator_role` always reads exactly one value, with no dependence on claim
order. `deploy/keycloak/realm/openclaw-realm.json` is configured this way.

### 2.4 ⚠️ Keycloak 26 silently discards undeclared attributes

This is the hardest one to diagnose. Keycloak 26 defaults to
`unmanagedAttributePolicy=None`, so writing through the Admin API an attribute that was
never declared in User Profile:

```
PUT /admin/realms/{realm}/users/{id}   ->  204 No Content    <- looks like it worked
GET /admin/realms/{realm}/users/{id}   ->  "attributes": null  <- actually not stored
```

The claim never reaches the token, OpenClaw sees viewer forever, and **neither side logs
anything**.

The `operator_role` attribute is copied into access tokens and determines authorization.
Never let end users edit it: doing so would let them grant themselves higher privileges.

Two safe ways out, pick one:

**A. Declare the attribute as administrator-editable (strict, preferred)**

```bash
# Declare operator_role through the realm's user profile configuration.
# Set its edit permission to ["admin"], not ["user"].
curl -X PUT "$KC/admin/realms/$REALM/users/profile" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  --data-binary @profile-with-operator-role.json
```

**B. Allow administrators to edit undeclared attributes**

```bash
cd deploy/keycloak && ./scripts/allow-unmanaged-attributes.sh
```

The script sets `unmanagedAttributePolicy` to `ADMIN_EDIT` and then re-reads every user to
confirm `operator_role` was actually stored. Unlike `ENABLED`, `ADMIN_EDIT` does not allow
end users to edit undeclared attributes. The script is idempotent.

Watch the field name: it is `validations` (plural) in the user profile configuration.
Writing `validation` returns a 400 with `Unrecognized field`.

---

## 3. Audience: configure and validate `aud`

Keycloak does not emit an `aud` claim for a client unless an audience mapper is configured.
The included realm adds a mapper that emits `aud=openclaw` for both `openclaw` (the browser
client) and `openclaw-verify` (a local-only password-grant client used by the verification
scripts). Configure OpenClaw to validate this audience:

```text
OpenClaw__Security__Oidc__Audience=openclaw
```

The `openclaw-verify` client exists only to support local verification. Do not retain or use
this password-grant client in production. For another client, configure an audience mapper
that emits `openclaw` or use the audience value you configure in OpenClaw.

Leaving `OidcConfig.Audience` empty remains an explicit option to skip validation. In earlier
versions leaving it empty failed outright:

```
SecurityTokenInvalidAudienceException: IDX10208:
Unable to validate audience. validationParameters.ValidAudience is null or whitespace
```

The cause: assigning null to `JwtBearerOptions.Audience` makes the validator enforce the
audience check. This is fixed (an empty value now sets `ValidateAudience = false` explicitly).

If you do want audience validation, add an **Audience mapper** to the Keycloak client so
`aud` is a fixed value, then set `Oidc.Audience` to that same value.

---

## 4. Verifying the role mapping

Verify with a real token after configuring. Do not stop at "sign-in succeeded".

```bash
# 1) Get a token (a client with direct access grants enabled, or grab it after browser sign-in)
TOKEN=$(curl -s -X POST "$KC/realms/$REALM/protocol/openid-connect/token" \
  --data-urlencode grant_type=password --data-urlencode client_id=openclaw-verify \
  --data-urlencode username=demo --data-urlencode password=*** --data-urlencode scope=openid \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')

# 2) Confirm the claim is actually there
python3 -c "
import base64,json
t='$TOKEN'; seg=t.split('.')[1]; seg+='='*(-len(seg)%4)
p=json.loads(base64.urlsafe_b64decode(seg))
print('operator_role =', p.get('operator_role'))
print('sub =', p.get('sub'))
"

# 3) Confirm the role actually affects authorization (admin-only endpoint)
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:18789/tools/approve?approvalId=x&approved=true"
# Expected: admin -> 404 (business error, authorization passed); operator -> 403 requires role 'admin'
# If RequireRequesterMatchForHttpToolApproval=true and requester identity is omitted,
# the endpoint returns 400 before the role probe can complete.
```

Measured result (`operator_role` switched from operator to admin for the same user):

| operator_role | HTTP | Response |
|---|---|---|
| `admin` | 404 | `No pending approval found for that id.` |
| `operator` | 403 | `Endpoint 'admin.approvals.override' requires role 'admin'.` |

With `RequireRequesterMatchForHttpToolApproval=true`, these probes omit requester identity
and return 400 before role-specific approval handling. The verification scripts report the
result as inconclusive and exit with status 2 because they cannot compare roles in that mode.

### 4.1 Live verification that multiple roles are not downgraded

`deploy/keycloak/scripts/verify-multi-role.sh` attaches `viewer` + `admin` realm roles to a
user through the Admin API, producing a genuine multi-valued claim:

```bash
cd deploy/keycloak
./scripts/verify-multi-role.sh http://localhost:18789   # gateway must run with RoleClaim=realm_roles
```

Measured output:

```
realm_roles = [viewer, admin, openclaw_admin]
old logic (FindFirstValue, first only) -> viewer     <- silently downgraded
new logic (FromRoles, highest)         -> admin
```

Sending the same token to a real gateway, `/tools/approve` responds:

| Code version | Resolved role | HTTP | Meaning |
|---|---|---|---|
| `FromRoles` (current) | `admin` | 404 | Passed the role check |
| `FindFirstValue` (mutated) | `viewer` | **403** | `requires role 'operator'` |
| viewer-only user | `viewer` | 403 | Negative control: proves the 404 is not a skipped check |

The last row is the important one: without it, a 404 could simply mean role checks are
disabled entirely.

---

## 5. Other differences you need to know

- **The `supabase.auth.*` API does not apply**: OpenClaw.NET has no Supabase SDK dependency.
  The built-in web chat uses an OIDC authorization-code flow with PKCE; the backend only
  validates the resulting tokens.
- **WebSocket needs `AllowQueryStringToken`**: browsers cannot set custom headers on a
  WebSocket handshake. Once enabled you can use `wss://host/ws?token=<jwt>`; evaluate the
  URL-leak risk for production.
- **Sign-out does not revoke tokens**: after a user is deleted or signs out of Keycloak, an
  already-issued token stays valid until `exp` (`accessTokenLifespan` defaults to 300
  seconds). For immediate revocation, shorten that value or add a state check.
- **`AuthMode=oidc` with an empty `Authority` now fails fast**: it reports
  `Security.AuthMode is "oidc" but Security.Oidc.Authority is empty`. Before the fix this
  crashed deep inside the middleware pipeline (`UnableToResolveService`).

---

## 6. Where the code lives

| Concern | File |
|---|---|
| Authentication service registration | `src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs` |
| Middleware activation | `src/OpenClaw.Gateway/Program.cs` (`UseAuthentication`) |
| Claim → identity mapping | `src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs` |
| Configuration model | `src/OpenClaw.Core/Models/GatewayConfig.cs` (`OidcConfig`) |
| Role semantics | `src/OpenClaw.Core/Models/OperatorGovernanceModels.cs` |
| Regression tests | `src/OpenClaw.Tests/OidcServiceRegistrationTests.cs`, `OidcRoleClaimResolutionTests.cs` |

---

简体中文版: [docs/zh-CN/KEYCLOAK.md](zh-CN/KEYCLOAK.md)
