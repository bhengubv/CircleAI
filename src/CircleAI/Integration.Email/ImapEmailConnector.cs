// ImapEmailConnector.cs
//
// (Phase B2) Generic IMAP client backed by MailKit. Works against any
// IMAP server (Fastmail, Posteo, ProtonMail Bridge, Mailcow, dovecot…)
// with username/password (typically an app-specific password).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace CircleAI.Integration.Email;

/// <param name="Host">IMAP host (e.g. "imap.fastmail.com").</param>
/// <param name="Port">Default 993 for IMAPS.</param>
/// <param name="UseSsl">Use SSL/TLS. Default true.</param>
/// <param name="Username">IMAP username.</param>
/// <param name="Password">IMAP password (app-specific is recommended).</param>
/// <param name="Folder">Folder to read. Default INBOX.</param>
public sealed record ImapOptions(
    string Host, int Port, bool UseSsl,
    string Username, string Password, string Folder = "INBOX");

public sealed class ImapEmailConnector : IEmailConnector
{
    private readonly ImapOptions _opts;

    public ImapEmailConnector(ImapOptions opts) => _opts = opts ?? throw new ArgumentNullException(nameof(opts));

    public string ProviderId   => "imap";
    public bool   IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.Host)
        && !string.IsNullOrWhiteSpace(_opts.Username)
        && !string.IsNullOrWhiteSpace(_opts.Password);

    public async ValueTask<IReadOnlyList<EmailMessage>> ListUnreadAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        using var client = new ImapClient();
        await ConnectAsync(client, ct).ConfigureAwait(false);
        var folder = await OpenFolderAsync(client, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct).ConfigureAwait(false);
        var slice = uids.OrderByDescending(u => u.Id).Take(max).ToList();
        var result = await FetchAsync(folder, slice, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(string query, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query required");
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        using var client = new ImapClient();
        await ConnectAsync(client, ct).ConfigureAwait(false);
        var folder = await OpenFolderAsync(client, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var uids = await folder.SearchAsync(
            SearchQuery.BodyContains(query).Or(SearchQuery.SubjectContains(query)),
            ct).ConfigureAwait(false);
        var slice = uids.OrderByDescending(u => u.Id).Take(max).ToList();
        var result = await FetchAsync(folder, slice, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("messageId required");
        if (!uint.TryParse(messageId, out var raw)) throw new ArgumentException("Expected an IMAP UID");
        using var client = new ImapClient();
        await ConnectAsync(client, ct).ConfigureAwait(false);
        var folder = await OpenFolderAsync(client, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        await folder.AddFlagsAsync(new UniqueId(raw), MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    private async Task ConnectAsync(ImapClient client, CancellationToken ct)
    {
        await client.ConnectAsync(_opts.Host, _opts.Port,
            _opts.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct).ConfigureAwait(false);
        await client.AuthenticateAsync(_opts.Username, _opts.Password, ct).ConfigureAwait(false);
    }

    private async Task<IMailFolder> OpenFolderAsync(ImapClient client, FolderAccess access, CancellationToken ct)
    {
        var folder = string.Equals(_opts.Folder, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(_opts.Folder, ct).ConfigureAwait(false);
        await folder.OpenAsync(access, ct).ConfigureAwait(false);
        return folder;
    }

    private static async Task<IReadOnlyList<EmailMessage>> FetchAsync(IMailFolder folder, IList<UniqueId> uids, CancellationToken ct)
    {
        var messages = new List<EmailMessage>(uids.Count);
        if (uids.Count == 0) return messages;
        var summaries = await folder.FetchAsync(uids,
            MessageSummaryItems.Envelope | MessageSummaryItems.Flags, ct).ConfigureAwait(false);
        foreach (var summary in summaries)
        {
            var env = summary.Envelope;
            var labels = new List<string>();
            if (summary.Flags is not null)
            {
                foreach (var flag in Enum.GetValues<MessageFlags>())
                    if (flag != MessageFlags.None && (summary.Flags.Value & flag) == flag) labels.Add(flag.ToString());
            }
            var bodyText = "";
            try
            {
                var msg = await folder.GetMessageAsync(summary.UniqueId, ct).ConfigureAwait(false);
                bodyText = msg.TextBody ?? msg.HtmlBody ?? "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ImapEmailConnector] body fetch failed for {summary.UniqueId}: {ex.Message}");
            }
            messages.Add(new EmailMessage(
                MessageId:    summary.UniqueId.Id.ToString(),
                From:         env?.From?.Mailboxes?.FirstOrDefault()?.Address ?? "",
                To:           env?.To?.Mailboxes?.Select(m => m.Address).ToArray() ?? Array.Empty<string>(),
                Subject:      env?.Subject ?? "",
                BodyText:     bodyText,
                ReceivedUtc:  env?.Date?.UtcDateTime ?? DateTime.UtcNow,
                Unread:       summary.Flags.HasValue && (summary.Flags.Value & MessageFlags.Seen) == 0,
                Labels:       labels));
        }
        return messages;
    }
}
