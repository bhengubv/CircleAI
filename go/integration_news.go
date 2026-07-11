// integration_news.go
//
// Ports CircleAI.Integration.News:
//   BlueskyOptions / BlueskySource   -> BlueskyOptions / BlueskySource
//   MastodonOptions / MastodonSource -> MastodonOptions / MastodonSource
//   NewsApiOptions / NewsApiSource   -> NewsApiOptions / NewsApiSource
//   RssOptions / RssNewsSource       -> RssOptions / RssNewsSource
//
// Each source is an INewsSource speaking a real HTTP feed API; the live HttpClient
// is replaced by the injected CarrierHTTP seam per the porting rules, so they are
// deterministic and make no network calls. Wire details (URLs, query params,
// auth/UA headers, JSON field extraction, HTML stripping, RSS/Atom parsing, the
// 80-char title truncation with an ellipsis, and the AT-URI → bsky.app URL
// mapping) are reproduced from the C# faithfully.

package circleai

import (
	"context"
	"encoding/xml"
	"errors"
	"strings"
)

// truncateTitle applies the C# "text.Length > 80 ? text[..80] + '…' : text" rule.
// Truncation is by rune (Unicode scalar) — exact for the ASCII/BMP text these
// feeds carry; the appended ellipsis is U+2026.
func truncateTitle(text string) string {
	r := []rune(text)
	if len(r) > 80 {
		return string(r[:80]) + "…"
	}
	return text
}

// ── Bluesky ─────────────────────────────────────────────────────────────────

// blueskyDefaultHost is the C# default AppView host.
const blueskyDefaultHost = "https://public.api.bsky.app"

// BlueskyOptions configures the Bluesky source. Ports BlueskyOptions. An empty
// Host defaults to the public AppView.
type BlueskyOptions struct {
	Query string
	Host  string
}

// BlueskySource reads Bluesky search results over the injected CarrierHTTP. Ports
// BlueskySource.
type BlueskySource struct {
	http CarrierHTTP
	opts BlueskyOptions
}

// NewBlueskySource constructs the source. http is required; an empty Host defaults
// to blueskyDefaultHost.
func NewBlueskySource(http CarrierHTTP, opts BlueskyOptions) (*BlueskySource, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if opts.Host == "" {
		opts.Host = blueskyDefaultHost
	}
	return &BlueskySource{http: http, opts: opts}, nil
}

// SourceID is "bluesky:{query}".
func (s *BlueskySource) SourceID() string { return "bluesky:" + s.opts.Query }

// IsConfigured is true when the Query is non-blank.
func (s *BlueskySource) IsConfigured() bool { return stringsTrimSpaceNonEmpty(s.opts.Query) }

// FetchLatest ports FetchLatestAsync: GET searchPosts?q=&limit=&sort=latest and
// map the "posts" array.
func (s *BlueskySource) FetchLatest(_ context.Context, max int) ([]NewsItem, error) {
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	url := s.opts.Host + "/xrpc/app.bsky.feed.searchPosts" +
		"?q=" + escapeDataString(s.opts.Query) + "&limit=" + itoaSmall(minInt(max, 100)) + "&sort=latest"
	resp, err := s.http.Do(&CarrierHTTPRequest{Method: "GET", URL: url})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Bluesky searchPosts", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	list := []NewsItem{}
	posts, ok := tjArray(root, "posts")
	if !ok {
		return list, nil
	}
	for _, p := range posts {
		post, ok := asJSONObject(p)
		if !ok {
			continue
		}
		uri, _ := tjString(post, "uri")
		record, _ := tjObject(post, "record")
		text := ""
		var ts string
		if record != nil {
			text, _ = tjString(record, "text")
			ts, _ = tjString(record, "createdAt")
		}
		var author string
		if a, ok := tjObject(post, "author"); ok {
			author, _ = tjString(a, "handle")
		}
		tags := []string{}
		if record != nil {
			if facets, ok := tjArray(record, "facets"); ok {
				for _, f := range facets {
					fm, ok := asJSONObject(f)
					if !ok {
						continue
					}
					if feats, ok := tjArray(fm, "features"); ok {
						for _, feat := range feats {
							if featm, ok := asJSONObject(feat); ok {
								if tag, ok := tjString(featm, "tag"); ok {
									tags = append(tags, tag)
								}
							}
						}
					}
				}
			}
		}
		sourceID := author
		if sourceID == "" {
			sourceID = s.SourceID()
		}
		list = append(list, NewsItem{
			ItemID:       uri,
			SourceID:     sourceID,
			Title:        truncateTitle(text),
			Summary:      text,
			URL:          buildBlueskyPostURL(author, uri),
			PublishedUtc: parseDateTimeOffsetUTC(ts),
			Tags:         tags,
		})
	}
	return list, nil
}

// buildBlueskyPostURL maps an AT-URI + handle to a bsky.app post URL. Ports
// BuildPostUrl; a missing handle/uri or a trailing-slash uri yields "about:blank".
func buildBlueskyPostURL(handle, atURI string) string {
	if !stringsTrimSpaceNonEmpty(handle) || !stringsTrimSpaceNonEmpty(atURI) {
		return "about:blank"
	}
	idx := strings.LastIndex(atURI, "/")
	if idx < 0 || idx == len(atURI)-1 {
		return "about:blank"
	}
	rkey := atURI[idx+1:]
	return "https://bsky.app/profile/" + handle + "/post/" + rkey
}

// ── Mastodon ────────────────────────────────────────────────────────────────

// MastodonOptions configures the Mastodon source. Ports MastodonOptions. Hashtag
// and AccessToken are optional (empty == absent).
type MastodonOptions struct {
	Instance    string
	Hashtag     string
	AccessToken string
}

// MastodonSource reads a Mastodon public/hashtag timeline over the injected
// CarrierHTTP. Ports MastodonSource.
type MastodonSource struct {
	http CarrierHTTP
	opts MastodonOptions
}

// NewMastodonSource constructs the source. http is required.
func NewMastodonSource(http CarrierHTTP, opts MastodonOptions) (*MastodonSource, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	return &MastodonSource{http: http, opts: opts}, nil
}

// SourceID is "mastodon:{instance}:public" or "mastodon:{instance}:#{hashtag}".
func (s *MastodonSource) SourceID() string {
	if s.opts.Hashtag == "" {
		return "mastodon:" + s.opts.Instance + ":public"
	}
	return "mastodon:" + s.opts.Instance + ":#" + s.opts.Hashtag
}

// IsConfigured is true when the Instance is non-blank.
func (s *MastodonSource) IsConfigured() bool { return stringsTrimSpaceNonEmpty(s.opts.Instance) }

// FetchLatest ports FetchLatestAsync: GET the public or tag timeline and map the
// status array (HTML content stripped to text).
func (s *MastodonSource) FetchLatest(_ context.Context, max int) ([]NewsItem, error) {
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	var path string
	if s.opts.Hashtag == "" {
		path = "/api/v1/timelines/public?limit=" + itoaSmall(minInt(max, 40))
	} else {
		path = "/api/v1/timelines/tag/" + escapeDataString(s.opts.Hashtag) + "?limit=" + itoaSmall(minInt(max, 40))
	}
	headers := map[string]string{"User-Agent": "CircleAI/1.0 (MastodonSource)"}
	if stringsTrimSpaceNonEmpty(s.opts.AccessToken) {
		headers["Authorization"] = "Bearer " + s.opts.AccessToken
	}
	resp, err := s.http.Do(&CarrierHTTPRequest{Method: "GET", URL: strings.TrimRight(s.opts.Instance, "/") + path, Headers: headers})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Mastodon timeline", resp.StatusCode)
	}
	arr, err := parseJSONArray(resp.Body)
	if err != nil {
		// A non-array body maps to an empty list (C# returns Array.Empty).
		return []NewsItem{}, nil
	}
	list := []NewsItem{}
	for _, raw := range arr {
		st, ok := asJSONObject(raw)
		if !ok {
			continue
		}
		url, _ := tjString(st, "url")
		contentHTML, _ := tjString(st, "content")
		pub, _ := tjString(st, "created_at")
		tags := []string{}
		if tagsArr, ok := tjArray(st, "tags"); ok {
			for _, tg := range tagsArr {
				if tgm, ok := asJSONObject(tg); ok {
					if tn, ok := tjString(tgm, "name"); ok {
						tags = append(tags, tn)
					}
				}
			}
		}
		var acct string
		if a, ok := tjObject(st, "account"); ok {
			acct, _ = tjString(a, "acct")
		}
		text := stripHTMLTags(contentHTML)
		sourceID := acct
		if sourceID == "" {
			sourceID = s.SourceID()
		}
		list = append(list, NewsItem{
			ItemID:       url,
			SourceID:     sourceID,
			Title:        truncateTitle(text),
			Summary:      text,
			URL:          absoluteOrBlank(url),
			PublishedUtc: parseDateTimeOffsetUTC(pub),
			Tags:         tags,
		})
	}
	return list, nil
}

// ── NewsAPI / GNews ─────────────────────────────────────────────────────────

// newsApiDefaultEndpoint is the C# default endpoint.
const newsApiDefaultEndpoint = "https://newsapi.org/v2/everything"

// NewsApiOptions configures the NewsAPI source. Ports NewsApiOptions. An empty
// Endpoint defaults to newsapi.org/v2/everything.
type NewsApiOptions struct {
	APIKey   string
	Query    string
	Endpoint string
}

// NewsApiSource reads a newsapi.org / gnews.io "articles" feed over the injected
// CarrierHTTP. Ports NewsApiSource.
type NewsApiSource struct {
	http CarrierHTTP
	opts NewsApiOptions
}

// NewNewsApiSource constructs the source. http is required; an empty Endpoint
// defaults to newsApiDefaultEndpoint.
func NewNewsApiSource(http CarrierHTTP, opts NewsApiOptions) (*NewsApiSource, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if opts.Endpoint == "" {
		opts.Endpoint = newsApiDefaultEndpoint
	}
	return &NewsApiSource{http: http, opts: opts}, nil
}

// SourceID is "newsapi:{query}".
func (s *NewsApiSource) SourceID() string { return "newsapi:" + s.opts.Query }

// IsConfigured is true when the APIKey is non-blank.
func (s *NewsApiSource) IsConfigured() bool { return stringsTrimSpaceNonEmpty(s.opts.APIKey) }

// FetchLatest ports FetchLatestAsync: GET the endpoint with q/pageSize/sortBy/
// language + X-Api-Key and map the "articles" array. Throws when unconfigured.
func (s *NewsApiSource) FetchLatest(_ context.Context, max int) ([]NewsItem, error) {
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	if !s.IsConfigured() {
		return nil, errors.New("NewsAPI key not configured.")
	}
	url := s.opts.Endpoint + "?q=" + escapeDataString(s.opts.Query) +
		"&pageSize=" + itoaSmall(minInt(max, 100)) + "&sortBy=publishedAt&language=en"
	headers := map[string]string{
		"X-Api-Key":  s.opts.APIKey,
		"User-Agent": "CircleAI/1.0 (NewsApiSource)",
	}
	resp, err := s.http.Do(&CarrierHTTPRequest{Method: "GET", URL: url, Headers: headers})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("NewsAPI everything", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	list := []NewsItem{}
	arr, ok := tjArray(root, "articles")
	if !ok {
		return list, nil
	}
	for _, a := range arr {
		art, ok := asJSONObject(a)
		if !ok {
			continue
		}
		title, _ := tjString(art, "title")
		desc, _ := tjString(art, "description")
		url2, _ := tjString(art, "url")
		pub, _ := tjString(art, "publishedAt")
		src := ""
		if sm, ok := tjObject(art, "source"); ok {
			src, _ = tjString(sm, "name")
		}
		sourceID := src
		if sourceID == "" {
			sourceID = s.SourceID()
		}
		list = append(list, NewsItem{
			ItemID:       url2,
			SourceID:     sourceID,
			Title:        title,
			Summary:      desc,
			URL:          absoluteOrBlank(url2),
			PublishedUtc: parseDateTimeOffsetUTC(pub),
			Tags:         []string{},
		})
	}
	return list, nil
}

// ── RSS / Atom ──────────────────────────────────────────────────────────────

// RssOptions configures the RSS/Atom source. Ports RssOptions. SourceID overrides
// the derived host-based id when set.
type RssOptions struct {
	FeedURL  string
	SourceID string
}

// RssNewsSource reads a generic RSS 2.0 / Atom 1.0 feed over the injected
// CarrierHTTP. Ports RssNewsSource.
type RssNewsSource struct {
	http CarrierHTTP
	opts RssOptions
}

// NewRssNewsSource constructs the source. http is required.
func NewRssNewsSource(http CarrierHTTP, opts RssOptions) (*RssNewsSource, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	return &RssNewsSource{http: http, opts: opts}, nil
}

// SourceID returns the configured SourceID, or the feed URL's host. Ports the
// getter (`_opts.SourceId ?? _opts.FeedUrl.Host`).
func (s *RssNewsSource) SourceID() string {
	if s.opts.SourceID != "" {
		return s.opts.SourceID
	}
	return uriHost(s.opts.FeedURL)
}

// IsConfigured is always true (ports the C# `=> true`).
func (s *RssNewsSource) IsConfigured() bool { return true }

// FetchLatest ports FetchLatestAsync: GET the feed, parse RSS items then Atom
// entries, concat, and take the first max.
func (s *RssNewsSource) FetchLatest(_ context.Context, max int) ([]NewsItem, error) {
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	resp, err := s.http.Do(&CarrierHTTPRequest{Method: "GET", URL: s.opts.FeedURL})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("RSS feed", resp.StatusCode)
	}
	sourceID := s.SourceID()
	items := append(parseRSS(resp.Body, sourceID), parseAtom(resp.Body, sourceID)...)
	if len(items) > max {
		items = items[:max]
	}
	if items == nil {
		items = []NewsItem{}
	}
	return items, nil
}

// xmlRSS mirrors the RSS 2.0 subset the C# ParseRss reads via
// doc.Descendants("item"). Elements are matched by local name (namespace-agnostic
// for the RSS core, as XName without a namespace does).
type xmlRSS struct {
	XMLName xml.Name    `xml:"rss"`
	Items   []xmlRSSItem `xml:"channel>item"`
}

type xmlRSSItem struct {
	Title       string   `xml:"title"`
	Link        string   `xml:"link"`
	PubDate     string   `xml:"pubDate"`
	Description string   `xml:"description"`
	GUID        string   `xml:"guid"`
	Categories  []string `xml:"category"`
}

// parseRSS ports ParseRss: each <item> → NewsItem (guid ?? link, description
// stripped). A non-RSS document yields no items.
func parseRSS(body []byte, sourceID string) []NewsItem {
	var feed xmlRSS
	if err := xml.Unmarshal(body, &feed); err != nil {
		return nil
	}
	var out []NewsItem
	for _, it := range feed.Items {
		guid := it.GUID
		if guid == "" {
			guid = it.Link
		}
		out = append(out, NewsItem{
			ItemID:       guid,
			SourceID:     sourceID,
			Title:        it.Title,
			Summary:      stripHTMLTags(it.Description),
			URL:          absoluteOrBlank(it.Link),
			PublishedUtc: parseDateTimeOffsetUTC(it.PubDate),
			Tags:         nonNilStrings(it.Categories),
		})
	}
	return out
}

// xmlAtom mirrors the Atom 1.0 subset the C# ParseAtom reads via
// doc.Descendants(atom + "entry"). encoding/xml matches the Atom namespace on the
// element name.
type xmlAtom struct {
	XMLName xml.Name       `xml:"http://www.w3.org/2005/Atom feed"`
	Entries []xmlAtomEntry `xml:"http://www.w3.org/2005/Atom entry"`
}

type xmlAtomEntry struct {
	Title     string          `xml:"http://www.w3.org/2005/Atom title"`
	Links     []xmlAtomLink   `xml:"http://www.w3.org/2005/Atom link"`
	Updated   string          `xml:"http://www.w3.org/2005/Atom updated"`
	Published string          `xml:"http://www.w3.org/2005/Atom published"`
	Summary   string          `xml:"http://www.w3.org/2005/Atom summary"`
	Content   string          `xml:"http://www.w3.org/2005/Atom content"`
	ID        string          `xml:"http://www.w3.org/2005/Atom id"`
	Categories []xmlAtomCategory `xml:"http://www.w3.org/2005/Atom category"`
}

type xmlAtomLink struct {
	Href string `xml:"href,attr"`
}

type xmlAtomCategory struct {
	Term string `xml:"term,attr"`
}

// parseAtom ports ParseAtom: each <entry> → NewsItem (updated ?? published,
// summary ?? content stripped, first link href, category terms).
func parseAtom(body []byte, sourceID string) []NewsItem {
	var feed xmlAtom
	if err := xml.Unmarshal(body, &feed); err != nil {
		return nil
	}
	var out []NewsItem
	for _, e := range feed.Entries {
		link := ""
		if len(e.Links) > 0 {
			link = e.Links[0].Href
		}
		pub := e.Updated
		if pub == "" {
			pub = e.Published
		}
		desc := e.Summary
		if desc == "" {
			desc = e.Content
		}
		guid := e.ID
		if guid == "" {
			guid = link
		}
		tags := []string{}
		for _, c := range e.Categories {
			if c.Term != "" {
				tags = append(tags, c.Term)
			}
		}
		out = append(out, NewsItem{
			ItemID:       guid,
			SourceID:     sourceID,
			Title:        e.Title,
			Summary:      stripHTMLTags(desc),
			URL:          absoluteOrBlank(link),
			PublishedUtc: parseDateTimeOffsetUTC(pub),
			Tags:         tags,
		})
	}
	return out
}

var (
	_ INewsSource = (*BlueskySource)(nil)
	_ INewsSource = (*MastodonSource)(nil)
	_ INewsSource = (*NewsApiSource)(nil)
	_ INewsSource = (*RssNewsSource)(nil)
)
