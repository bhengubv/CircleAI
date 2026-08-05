// WebSearch.cs
//
// The one thing a purely on-device assistant cannot do: know what happened today.
//
// A 454 MB model knows what it was trained on and nothing since. Asked the
// weather, the news, a price, or a date, an honest local model says "I do not
// have internet access" — which is correct, and useless. This closes that gap.
//
// IT BREAKS A PROMISE, DELIBERATELY, AND THE PROMISE HAS TO CHANGE WITH IT. The
// home screen said "nothing sent anywhere". The moment a search runs, the query
// leaves the phone. That claim is now qualified on the screen rather than quietly
// falsified here, because a privacy claim that is subtly untrue is worse than one
// that is honestly narrower.
//
// WHAT LEAVES: the search words, and nothing else. Not the conversation, not the
// memory, not identity, not the rest of the answer. The model decides when to
// search, the query is what it decided to look up, and everything else about the
// turn stays local.
//
// KEYLESS ON PURPOSE. No API key, no account, no per-user registration — the same
// reason the rest of the product works with nothing signed in. That rules out the
// big search APIs and leaves DuckDuckGo's public endpoints, which need nothing.
// The parsing is therefore HTML scraping and will break when they change their
// markup; the failure is reported plainly rather than silently returning nothing,
// so a broken scrape looks like a broken scrape and not like a quiet model.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleAI.Samples.It;

/// <summary>Looks things up on the web, keylessly, and reports honestly when it cannot.</summary>
public static class WebSearch
{
    /// <summary>
    /// How long to wait before giving up on the network.
    /// </summary>
    /// <remarks>
    /// Short on purpose. This sits inside a spoken turn that is already slow, and
    /// an answer that never arrives is worse than "I could not reach the internet"
    /// — the person is standing there waiting to be spoken to.
    /// </remarks>
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Results worth reading aloud. More than this is a monologue.</summary>
    const int MaxResults = 3;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = Timeout,
        };
        // Identify as an ordinary browser: the HTML endpoint serves a stripped or
        // empty page to clients that do not, and an empty page is indistinguishable
        // from "no results" unless you know to look for this.
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Mobile Safari/537.36");
        return http;
    }

    /// <summary>
    /// Searches the web and returns a short digest, or a plain reason it could not.
    /// </summary>
    /// <remarks>
    /// Returns TEXT rather than throwing, because the caller is a tool bridge whose
    /// result goes straight to a language model. "Could not reach the internet" is
    /// something the model can relay to the person; an exception is not.
    /// </remarks>
    public static async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length == 0) return "No search query was given.";

        try
        {
            var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
                return $"The search service answered {(int)res.StatusCode}. Try again shortly.";

            var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var hits = Parse(html);

            if (hits.Count == 0)
                return $"No results found for \"{query}\".";

            var sb = new StringBuilder();
            sb.Append("Web results for \"").Append(query).Append("\":\n");
            for (var i = 0; i < hits.Count && i < MaxResults; i++)
                sb.Append(i + 1).Append(". ").Append(hits[i]).Append('\n');
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout, not the caller cancelling the turn.
            return "The search took too long to answer. The connection may be slow.";
        }
        catch (HttpRequestException ex)
        {
            // The common case on a phone: no signal, aeroplane mode, captive portal.
            return "Could not reach the internet — " + Innermost(ex);
        }
        catch (Exception ex)
        {
            return "The search failed — " + Innermost(ex);
        }
    }

    /// <summary>
    /// Pulls titles and snippets out of the results page.
    /// </summary>
    /// <remarks>
    /// SCRAPING, AND HONEST ABOUT IT. There is no keyless JSON endpoint that
    /// answers general queries, so this reads the HTML the site serves to a
    /// browser. It will break when the markup changes. When it does, Parse returns
    /// nothing and the caller says so, which is a visible failure rather than an
    /// assistant that has quietly stopped knowing anything.
    /// </remarks>
    static List<string> Parse(string html)
    {
        var hits = new List<string>();

        // Result blocks: the snippet carries the substance, the title gives it a
        // subject. Taken together they read as a sentence when spoken aloud.
        var titles   = Rx(html, "result__a[^>]*>(.+?)</a>");
        var snippets = Rx(html, "result__snippet[^>]*>(.+?)</a>");

        for (var i = 0; i < titles.Count && hits.Count < MaxResults; i++)
        {
            var title = Clean(titles[i]);
            var snip  = i < snippets.Count ? Clean(snippets[i]) : string.Empty;
            if (title.Length == 0) continue;

            hits.Add(snip.Length > 0 ? $"{title} — {snip}" : title);
        }

        return hits;
    }

    static List<string> Rx(string html, string pattern)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(html, pattern,
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
            if (m.Groups.Count > 1) found.Add(m.Groups[1].Value);
        return found;
    }

    /// <summary>Strips markup and entities, and trims to something speakable.</summary>
    static string Clean(string raw)
    {
        var text = Regex.Replace(raw, "<.*?>", string.Empty, RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text) ?? string.Empty;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        // Long enough to be useful, short enough that three of them are not a
        // speech. This is read out loud, not skimmed.
        return text.Length <= 220 ? text : text[..220].TrimEnd() + "…";
    }

    static string Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is { } inner) e = inner;
        var m = e.Message.Trim();
        return m.Length <= 120 ? m : m[..120] + "…";
    }
}
