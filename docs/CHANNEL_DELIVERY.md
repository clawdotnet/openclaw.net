# Channel delivery outcomes

`IChannelAdapter.SendAsync` keeps its existing signature. Normal completion means
that every supported part was accepted by the adapter's transport. It does not
prove that a recipient read or received the message. WebSocket acceptance means a
completed frame write; cron acceptance means an appended local output record.

- **Accepted:** normal completion. Only this outcome advances heartbeat success.
- **Suppressed:** `ChannelDeliverySuppressedException`, for intentional policy
  decisions such as SMS allowlists or an opted-out contact. Never retry or record
  delivery success.
- **Rejected:** `ChannelDeliveryException` with a known provider rejection or
  local validation error. Only explicit transient rejections before any accepted
  part may retry; provider retry delays are respected.
- **Partial:** the exception's `DeliveredParts` counts confirmed accepted parts.
  The worker never replays the whole message.
- **Unknown:** transport loss, malformed acknowledgment, or an unclassified
  plugin error may follow provider acceptance. Never automatically retry.
- **Canceled:** caller cancellation propagates as `OperationCanceledException`.
  Cancellation does not prove that a send did not reach the provider.

The worker retains its two-attempt limit, but generic exceptions no longer opt
adapters into retries. Native plugins should use the same typed exception contract.
Bridged plugins must provide a send handler and complete it only after acceptance;
bridge errors and missing acknowledgments are not successes. A failed hybrid-transport
channel send is not replayed over stdio; switching transports applies only to
subsequent operations.

## Intentional behavior changes

Slack, Discord, Teams, Feishu, DingTalk, WeCom, Signal, WhatsApp, email, SMS, cron,
and final WebSocket sends propagate delivery outcomes rather than swallowing them.
The existing Telegram outcome contract is retained, with cancellation checked
before dispatch and unsupported media rejected rather than silently omitted.

`WhatsApp.BridgeSuppressSendExceptions` remains readable for configuration
compatibility, but no longer hides failed outbound sends. Provider errors must be
visible to the worker. A 2xx HTTP bridge response is its transport acknowledgment;
bridge operators must return non-2xx for rejected requests.

Unsupported media or oversized content is rejected rather than silently dropped
or truncated. Discord text is split into provider-sized chunks. WhatsApp Cloud
requires one media attachment per send, and rejects accompanying text for media
kinds that cannot carry captions. Slack and Discord adapters currently support
text only, so media markers are rejected explicitly.

The current signald and WeCom WebSocket paths do not correlate provider delivery
acknowledgments. A written request is therefore reported as **unknown**, not
confirmed acceptance. In particular, WeCom does not fall back to another route
after a potentially successful socket write. Signal CLI requires successful exit;
nonzero exit is unknown because dispatch may already have occurred.

This change does not introduce durable outbound storage, provider receipts,
exactly-once delivery, or automatic resumption of individual unsent parts. It adds
no packages or reflection in production runtime paths and preserves NativeAOT/JIT
compatibility. Regression tests use synthetic providers; live-provider acceptance
remains deployment-specific.
