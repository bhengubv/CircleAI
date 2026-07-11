#ifndef CIRCLE_AI_AGENTS_PEER_H
#define CIRCLE_AI_AGENTS_PEER_H

/*
 * agents_peer.h — CircleAI.Agents.Peer (C11 port).
 *
 * Ports the agent-to-agent mesh protocol:
 *     AgentMessage.cs            : AgentMessageKind + AgentMessage(+Create).
 *     PeerAgent.cs               : PeerAgent + AgentCapability.
 *     AgentInvocationException.cs: modelled as return codes + a decline envelope.
 *     IAgentPeerProtocol.cs      : the protocol contract.
 *     AgentBus.cs                : in-process peer registry + per-peer inboxes.
 *     InMemoryAgentPeerProtocol.cs: the reference protocol implementation.
 *
 * PREFIX NOTE: the existing agents.h already defines a simpler, unrelated
 * ca_agent_message_t (a 1.5.0 fixture). To avoid any collision, EVERYTHING here
 * uses the distinct ca_peer_* prefix (ca_peer_message_t, ca_peer_agent_t, ...),
 * and this header does NOT include agents.h.
 *
 * ── Async -> sync model (house rule: async methods complete synchronously) ──
 *
 * The C# stack is built on Task / Channel / a background pump thread. C is pure
 * libc with no threads, so delivery is SYNCHRONOUS but DECOUPLED and PUMP-DRIVEN:
 *
 *   - AgentBus keeps a per-peer inbox FIFO. Send enqueues; nothing is delivered
 *     until a consumer drains the inbox. (Mirrors the unbounded C# Channel.)
 *
 *   - A protocol does NOT spawn a pump thread (the C# Task.Run(PumpInboxAsync)).
 *     Instead the caller drives ca_peer_protocol_pump(proto), which drains THIS
 *     proto's inbox once, updates last-seen, handles each message, and buffers
 *     externally-surfaced messages for ca_peer_protocol_try_read_inbox.
 *
 *   - Because two protocols share one bus, an Invoke round-trip needs the caller
 *     to pump BOTH: pump the target (so it runs its capability handler and Sends
 *     a Response/Decline back), then pump the invoker (so it matches the reply to
 *     the pending invocation). ca_peer_protocol_invoke therefore Sends + registers
 *     a pending invocation and returns CA_PEER_OK immediately with the Invoke.Id;
 *     ca_peer_protocol_try_take_reply then yields the Response (CA_PEER_OK) or the
 *     Decline (CA_PEER_DECLINED) once both have been pumped, or false while pending.
 *
 * Why this is faithful: the C# semantics are (a) validate reachability, (b) Send an
 * Invoke, (c) await the matching Response/Decline, (d) throw on Decline. This port
 * preserves every step and the Guid-prefix correlation exactly; only the *await*
 * becomes an explicit pump + poll. The 5-second InvokeTimeout is OMITTED: a timeout
 * is not expressible without a clock or threads, and there is no ambient monotonic
 * clock in this pure-libc port. A caller that has pumped both sides and still gets
 * `false` from try_take_reply is in the state the C# await would have timed out on.
 *
 * ── Other adaptations ──
 *   - AgentInvocationException -> a ca_peer_error_t return code + an out decline
 *     envelope. { CA_PEER_OK, CA_PEER_UNREACHABLE, CA_PEER_TIMEOUT, CA_PEER_DECLINED }.
 *     CA_PEER_TIMEOUT is declared for parity but never produced (timeout omitted).
 *   - StreamInboxAsync -> ca_peer_protocol_try_read_inbox (drain, false when empty).
 *   - Bus.Receive (async stream) -> ca_peer_bus_try_receive (dequeue one, false when
 *     empty). Synchronous drain — the pump calls it in a loop.
 *   - AgentMessage.Id / CorrelationId: Id is 16 random-ish bytes; CorrelationId,
 *     when not supplied, is a fresh 32-lowercase-hex string (Guid "n" format). Both
 *     are filled from a time+counter-seeded PRNG — portable, NOT cryptographic; the
 *     mesh only needs uniqueness within a run, which this provides.
 *   - AgentCapability.CostPerInvocation: C# decimal -> double. CAVEAT: double cannot
 *     represent every decimal exactly; fine for advertised costs, but do not use it
 *     for exact-cent settlement (that stays in the C# / SDPKT ledger).
 *   - SentAt / LastSeenAt: DateTimeOffset -> int64 Unix ms UTC, passed in (explicit
 *     clock — there is no ambient UtcNow).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free / *_free_array, deep-copy getters, errors via NULL / -1 / false /
 * count SIZE_MAX. to_uhid / correlation_id / current_transport_id may be NULL where
 * noted (the C# null / "*" broadcast is a literal "*"). Linear arrays, no hashtable,
 * no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * AgentMessageKind
 * =========================================================================== */

typedef enum {
    CA_PEER_MSG_DISCOVER         = 0,
    CA_PEER_MSG_GREET            = 1,
    CA_PEER_MSG_CAPABILITY_QUERY = 2,
    CA_PEER_MSG_INVOKE           = 3,
    CA_PEER_MSG_RESPONSE         = 4,
    CA_PEER_MSG_DECLINE          = 5,
    CA_PEER_MSG_HEARTBEAT        = 6
} ca_peer_message_kind_t;

/* Invocation outcome (models AgentInvocationException as a code). CA_PEER_TIMEOUT
 * is declared for parity with the C# timeout path but is never produced here — the
 * timeout is omitted (no clock/threads). */
typedef enum {
    CA_PEER_OK          = 0,
    CA_PEER_UNREACHABLE = 1,   /* peer not on the bus */
    CA_PEER_TIMEOUT     = 2,   /* declared for parity; never produced */
    CA_PEER_DECLINED    = 3    /* peer returned a Decline envelope */
} ca_peer_error_t;

/* ===========================================================================
 * AgentMessage
 * =========================================================================== */

/* AgentMessage(Guid Id, Kind, FromUhid, ToUhid, ContentType, byte[] Payload,
 * byte[] Signature, DateTimeOffset SentAt){ string? CorrelationId }.
 * to_uhid is "*" for a broadcast. correlation_id may be NULL. payload / signature
 * may be NULL when their length is 0. */
typedef struct {
    uint8_t                id[16];
    ca_peer_message_kind_t kind;
    char                  *from_uhid;      /* owned, non-null */
    char                  *to_uhid;        /* owned, non-null ("*" == broadcast) */
    char                  *content_type;   /* owned, non-null */
    uint8_t               *payload;        /* owned; NULL when payload_len == 0 */
    size_t                 payload_len;
    uint8_t               *signature;      /* owned; NULL when signature_len == 0 */
    size_t                 signature_len;
    int64_t                sent_at_ms;     /* DateTimeOffset as Unix ms UTC */
    char                  *correlation_id; /* owned; NULL ok */
} ca_peer_message_t;

void ca_peer_message_free(ca_peer_message_t *m);
void ca_peer_message_free_array(ca_peer_message_t *arr, size_t count);
/* Deep-copy src into dst (dst assumed uninitialised). false on OOM. */
bool ca_peer_message_copy(ca_peer_message_t *dst, const ca_peer_message_t *src);

/* AgentMessage.Create(kind, fromUhid, toUhid, contentType, payload, sig,
 * correlationId=null): fills a fresh random 16-byte Id, stamps sent_at_ms = now_ms,
 * and sets correlation_id = correlation_id ?? <fresh 32-hex>. payload / signature
 * are deep-copied (may be NULL when their length is 0). Writes into *out (assumed
 * uninitialised). Returns 0 on success, -1 on bad args / OOM. */
int ca_peer_message_create(ca_peer_message_t *out,
                           ca_peer_message_kind_t kind,
                           const char *from_uhid, const char *to_uhid,
                           const char *content_type,
                           const uint8_t *payload, size_t payload_len,
                           const uint8_t *signature, size_t signature_len,
                           const char *correlation_id /* NULL ok */,
                           int64_t now_ms);

/* ===========================================================================
 * AgentCapability / PeerAgent
 * =========================================================================== */

/* AgentCapability(Name, Version, decimal CostPerInvocation, CostCurrency).
 * cost_per_invocation is a double (C# decimal -> double; see the header caveat). */
typedef struct {
    char  *name;             /* owned, non-null */
    char  *version;          /* owned, non-null */
    double cost_per_invocation;
    char  *cost_currency;    /* owned, non-null */
} ca_peer_capability_t;

void ca_peer_capability_free(ca_peer_capability_t *c);
void ca_peer_capability_free_array(ca_peer_capability_t *arr, size_t count);

/* PeerAgent(Guid Id, UhidIdentityId, DisplayName, Capabilities[], PublicKeyDer,
 * CurrentTransportId?, DateTimeOffset LastSeenAt). current_transport_id may be NULL
 * (the C# null == offline). public_key_der may be NULL when public_key_len == 0. */
typedef struct {
    uint8_t               id[16];
    char                 *uhid_identity_id;   /* owned, non-null */
    char                 *display_name;       /* owned, non-null */
    ca_peer_capability_t *capabilities;       /* owned array */
    size_t                capabilities_count;
    uint8_t              *public_key_der;     /* owned; NULL when len == 0 */
    size_t                public_key_len;
    char                 *current_transport_id; /* owned; NULL ok */
    int64_t               last_seen_at_ms;    /* DateTimeOffset as Unix ms UTC */
} ca_peer_agent_t;

void ca_peer_agent_free(ca_peer_agent_t *a);
void ca_peer_agent_free_array(ca_peer_agent_t *arr, size_t count);
/* Deep-copy src into dst (dst assumed uninitialised). false on OOM. */
bool ca_peer_agent_copy(ca_peer_agent_t *dst, const ca_peer_agent_t *src);

/* ===========================================================================
 * AgentBus — in-process registry + per-peer inbox FIFO
 * =========================================================================== */

typedef struct ca_peer_bus ca_peer_bus_t;

/* new AgentBus(). NULL on OOM. */
ca_peer_bus_t *ca_peer_bus_create(void);
void ca_peer_bus_destroy(ca_peer_bus_t *bus);

/* Register(peer): add/replace by UhidIdentityId (Ordinal) + ensure an inbox exists.
 * Deep-copies peer. Returns 0 on success, -1 on bad args / OOM. */
int ca_peer_bus_register(ca_peer_bus_t *bus, const ca_peer_agent_t *peer);

/* Unregister(uhid): remove the peer record and drop its inbox (any buffered
 * messages are freed). uhid must be non-null / non-whitespace. Returns 0 (no-op
 * when absent), -1 on bad args. */
int ca_peer_bus_unregister(ca_peer_bus_t *bus, const char *uhid);

/* TryGetPeer(uhid): writes a fresh owned copy into *out and returns true; false
 * when absent / bad args (with *out zeroed). Caller frees *out with
 * ca_peer_agent_free. */
bool ca_peer_bus_try_get_peer(const ca_peer_bus_t *bus, const char *uhid,
                             ca_peer_agent_t *out);

/* RegisteredPeers: fresh owned snapshot array (*out_count). NULL + 0 when empty;
 * NULL + SIZE_MAX on error. Caller frees with ca_peer_agent_free_array. */
ca_peer_agent_t *ca_peer_bus_registered_peers(const ca_peer_bus_t *bus,
                                             size_t *out_count);

/* Send(message): if ToUhid == "*", enqueue a copy into every inbox except the
 * sender's (FromUhid). Else enqueue into the target inbox if it exists; an unknown
 * target is dropped silently. Deep-copies on enqueue. Returns 0 on success (incl.
 * a silent drop), -1 on bad args / OOM. */
int ca_peer_bus_send(ca_peer_bus_t *bus, const ca_peer_message_t *message);

/* Receive(uhid) one step: dequeue the next message for uhid into *out and return
 * true; false when the inbox is empty / unknown / bad args. Caller frees *out with
 * ca_peer_message_free. */
bool ca_peer_bus_try_receive(ca_peer_bus_t *bus, const char *uhid,
                            ca_peer_message_t *out);

/* Number of buffered messages in a peer's inbox (0 if unknown). */
size_t ca_peer_bus_inbox_count(const ca_peer_bus_t *bus, const char *uhid);

/* ===========================================================================
 * InMemoryAgentPeerProtocol
 * =========================================================================== */

typedef struct ca_peer_protocol ca_peer_protocol_t;

/* Optional signer seam (Func<byte[],byte[]>). Given data[len], allocate *out_sig of
 * *out_len bytes (malloc) and return 0; return non-0 on failure. A NULL signer means
 * an empty signature (the C# `[]`). */
typedef int (*ca_peer_signer_fn)(void *ctx, const uint8_t *data, size_t len,
                                 uint8_t **out_sig, size_t *out_len);

/* Optional capability handler seam (Func<AgentCapability,byte[],byte[]>). Given the
 * capability and the request payload[payload_len], allocate *out_result of *out_len
 * bytes (malloc) and return 0 to send a Response; return non-0 to send a Decline
 * (the C# null return). */
typedef int (*ca_peer_capability_handler_fn)(void *ctx,
                                             const ca_peer_capability_t *cap,
                                             const uint8_t *payload,
                                             size_t payload_len,
                                             uint8_t **out_result,
                                             size_t *out_len);

/* new InMemoryAgentPeerProtocol(ownUhid, bus, ownCapabilities, ownPublicKey,
 * signer?, capabilityHandler?). Registers a self PeerAgent on the bus
 * (CurrentTransportId "in-memory", DisplayName = ownUhid, LastSeenAt = now_ms).
 * ownUhid must be non-null / non-whitespace; bus non-null; caps may be NULL when
 * ncaps == 0; pubkey may be NULL when pubkey_len == 0. ctx is passed to both seams.
 * now_ms stamps the self-registration. Returns NULL on bad args / OOM. */
ca_peer_protocol_t *ca_peer_protocol_create(
    const char *own_uhid, ca_peer_bus_t *bus,
    const ca_peer_capability_t *caps, size_t ncaps,
    const uint8_t *pubkey, size_t pubkey_len,
    ca_peer_signer_fn signer, ca_peer_capability_handler_fn capability_handler,
    void *ctx, int64_t now_ms);

/* Dispose(): unregisters this proto's self peer from the bus and frees the proto. */
void ca_peer_protocol_destroy(ca_peer_protocol_t *proto);

/* The UHID identity owned by this proto (OwnUhid). Borrowed; valid for the proto's
 * lifetime. */
const char *ca_peer_protocol_own_uhid(const ca_peer_protocol_t *proto);

/*
 * Drive the inbox once (the C# background PumpInboxAsync, made explicit). Drains
 * every buffered message for this proto from the bus, and for each:
 *   - updates last-seen for message.FromUhid to message.sent_at_ms;
 *   - Response/Decline -> completes a matching pending invocation (matched by the
 *     16-byte Guid prefix of the payload);
 *   - Invoke -> routes to the capability handler and Sends back a Response (handler
 *     returned 0) or a Decline (non-0 / no handler), correlation-prefixed with the
 *     original Invoke.Id;
 *   - every message is also buffered for ca_peer_protocol_try_read_inbox.
 * now_ms is unused by the pump itself (last-seen comes from each message's sent_at)
 * but reserved for signing any outbound reply's timestamp. Returns the number of
 * messages processed, or -1 on bad args / a fatal OOM. */
int ca_peer_protocol_pump(ca_peer_protocol_t *proto, int64_t now_ms);

/* StreamInboxAsync one step: drain the next externally-surfaced message into *out
 * and return true; false when empty / bad args. Only messages seen by a prior
 * ca_peer_protocol_pump are surfaced here. Caller frees *out with
 * ca_peer_message_free. */
bool ca_peer_protocol_try_read_inbox(ca_peer_protocol_t *proto,
                                    ca_peer_message_t *out);

/* DiscoverPeersAsync: Send a signed Discover broadcast (empty payload), then return
 * bus.RegisteredPeers excluding self, each with LastSeenAt overlaid from this proto's
 * local last-seen map. now_ms stamps the broadcast. Returns a fresh owned array
 * (*out_count); NULL + 0 when none; NULL + SIZE_MAX on error. Caller frees with
 * ca_peer_agent_free_array. */
ca_peer_agent_t *ca_peer_protocol_discover(ca_peer_protocol_t *proto,
                                          int64_t now_ms, size_t *out_count);

/* GreetAsync(targetUhid): if the peer is unknown -> false (the C# null). Else Send a
 * signed Greet (empty payload) and write the peer (LastSeen overlaid) into *out,
 * returning true. now_ms stamps the greet. Caller frees *out with
 * ca_peer_agent_free. */
bool ca_peer_protocol_greet(ca_peer_protocol_t *proto, const char *target_uhid,
                           int64_t now_ms, ca_peer_agent_t *out);

/* QueryCapabilitiesAsync(targetUhid): if the peer is unknown -> empty (NULL + 0).
 * Else return the peer's advertised capabilities. Fresh owned array (*out_count);
 * NULL + SIZE_MAX on error. Caller frees with ca_peer_capability_free_array. */
ca_peer_capability_t *ca_peer_protocol_query_capabilities(
    ca_peer_protocol_t *proto, const char *target_uhid, size_t *out_count);

/* InvokeAsync(targetUhid, capability, requestPayload) — part 1 of the pump-driven
 * round-trip. If the peer is unknown -> CA_PEER_UNREACHABLE. Else build a signed
 * Invoke (contentType "application/octet-stream", payload = requestPayload), register
 * a pending invocation keyed by Invoke.Id, Send it, copy Invoke.Id into out_invoke_id,
 * and return CA_PEER_OK. The caller then pumps the target, pumps this proto, and calls
 * ca_peer_protocol_try_take_reply(out_invoke_id). now_ms stamps the Invoke.
 * requestPayload may be NULL when request_len == 0. Returns CA_PEER_UNREACHABLE, or
 * CA_PEER_OK on a successful Send (and out_invoke_id filled). */
ca_peer_error_t ca_peer_protocol_invoke(ca_peer_protocol_t *proto,
                                       const char *target_uhid,
                                       const ca_peer_capability_t *capability,
                                       const uint8_t *request_payload,
                                       size_t request_len,
                                       uint8_t out_invoke_id[16], int64_t now_ms);

/* InvokeAsync — part 2. Once both protos have been pumped, look up the pending
 * invocation for invoke_id:
 *   - Response arrived -> writes it into *out, sets *err = CA_PEER_OK, returns true.
 *   - Decline arrived  -> writes it into *out, sets *err = CA_PEER_DECLINED, returns
 *     true (the C# AgentInvocationException carrying the decline envelope).
 *   - still pending     -> returns false (no *out written). This is the state the C#
 *     5s timeout would eventually have fired on — omitted here.
 * A consumed reply is removed from the pending table. *out (when written) is caller-
 * freed with ca_peer_message_free. *err may be NULL if not wanted. */
bool ca_peer_protocol_try_take_reply(ca_peer_protocol_t *proto,
                                    const uint8_t invoke_id[16],
                                    ca_peer_message_t *out, ca_peer_error_t *err);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AGENTS_PEER_H */
