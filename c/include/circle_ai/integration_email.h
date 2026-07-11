#ifndef CIRCLE_AI_INTEGRATION_EMAIL_H
#define CIRCLE_AI_INTEGRATION_EMAIL_H

/*
 * integration_email.h — CircleAI.Integration.Email (C11 port).
 *
 * Deterministic in-memory IEmailConnector implementations standing in for the
 * three connectors (GmailEmailConnector, ImapEmailConnector, MsGraphEmailConnector).
 * The real connectors read Gmail v1 / IMAP / Microsoft Graph; here the mailbox is
 * an in-memory message array (populated via ca_int_email_seed — the injected
 * network data) and the contract matches: ProviderId, IsConfigured, ListUnread,
 * Search, MarkRead.
 *
 *   Provider ids : "gmail", "imap", "ms-graph-mail".
 *   IsConfigured : Gmail   := AccessTokenProvider is not null;
 *                  Imap    := host && username && password all non-blank;
 *                  MsGraph := AccessTokenProvider is not null.
 *   ListUnread(max)   : Unread messages, newest-first (ReceivedUtc desc),
 *                       Take(max). max<=0 -> ArgumentOutOfRangeException.
 *                       (Gmail routes ListUnread through Search("is:unread").)
 *   Search(query,max) : Subject OR BodyText OrdinalIgnoreCase substring,
 *                       newest-first, Take(max). query NULL/whitespace or
 *                       max<=0 -> error.
 *   MarkRead(id)      : sets Unread=false for the matching MessageId (unknown id
 *                       swallowed). id NULL/whitespace -> ArgumentException.
 *
 * Conventions per integration.h. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>

#include "integration.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Create an in-memory Gmail connector (ProviderId "gmail").
 * has_token_provider mirrors "AccessTokenProvider is not null". NULL on OOM. */
ca_int_email_connector_t *ca_int_gmail_email_create(bool has_token_provider);

/* Create an in-memory IMAP connector (ProviderId "imap").
 * IsConfigured := host && username && password all non-blank; any may be NULL. */
ca_int_email_connector_t *ca_int_imap_email_create(const char *host,
                                                   const char *username,
                                                   const char *password);

/* Create an in-memory Microsoft Graph mail connector (ProviderId "ms-graph-mail"). */
ca_int_email_connector_t *ca_int_msgraph_email_create(bool has_token_provider);

/* Seed the connector's mailbox with a message (deep-copied; the injected network
 * payload). 0 success; -1 bad args/OOM. Available on any of the three above. */
int ca_int_email_seed(ca_int_email_connector_t *c,
                      const ca_int_email_message_t *msg);

/* Destroy any email connector returned above (frees mailbox + vtable). */
void ca_int_email_connector_destroy(ca_int_email_connector_t *c);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_EMAIL_H */
