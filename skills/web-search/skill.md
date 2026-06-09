---
name: "web-search"
description: "Multi-engine web search with 17 engines, region filtering, time filters, site-specific search, and WolframAlpha knowledge queries. No API keys required for basic scraping-based engines."
allowedTools: ["WebSearch", "WebFetch"]
---

# Web Search Skill

Enhanced web search with 15+ search engines across CN and global regions.

## Basic Usage

```
WebSearch(query="python tutorial", region="all")
WebSearch(query="人工智能", region="cn")
WebSearch(query="machine learning", region="global")
```

## Parameters

| Parameter | Required | Default | Description |
|-----------|----------|---------|-------------|
| `query` | ✅ | — | Search keywords |
| `region` | — | `"all"` | `"cn"` / `"global"` / `"all"` |
| `timeFilter` | — | `null` | `"hour"` / `"day"` / `"week"` / `"month"` / `"year"` |
| `site` | — | `null` | Limit to site (e.g. `"github.com"`) |
| `topK` | — | `5` | Results per engine (1-10) |

## 17 Search Engines

**Domestic (CN):** Baidu, Bing CN, Sogou, 360, Toutiao, WeChat, Google HK
**Global:** Google, Bing INT, DuckDuckGo, Brave, Yahoo, Startpage, Ecosia, Qwant
**Knowledge:** WolframAlpha (auto-detected for math/convert/stock queries)

## Region Strategy

- `region="cn"` — Prioritize Baidu, Sogou, 360. Best for Chinese-language content
- `region="global"` — Prioritize Google, DuckDuckGo, Brave. Best for English/tech content
- `region="all"` (default) — Try all engines. Most comprehensive

## Time Filter Examples

```
# Past week
WebSearch(query="AI breakthroughs", timeFilter="week")

# Past day  
WebSearch(query="technology news", timeFilter="day")

# Past year
WebSearch(query="machine learning trends", timeFilter="year")
```

## Site-Specific Examples

```
# Search within GitHub
WebSearch(query="tensorflow", site="github.com")

# Search Wikipedia
WebSearch(query="quantum computing", site="en.wikipedia.org")

# Search documentation
WebSearch(query="async/await", site="learn.microsoft.com")
```

## WolframAlpha Knowledge Queries

Automatically detected when query contains:

| Type | Example |
|------|---------|
| Math | `integrate x^2 dx`, `solve x^2-5x+6=0` |
| Conversion | `100 USD to CNY`, `100 miles to km` |
| Stocks | `AAPL stock`, `Tesla stock` |
| Weather | `weather in Beijing` |
| Data | `population of China`, `GDP of China vs USA` |
| Nutrition | `calories in banana` |

## Advanced Search Operators

Used within `query` parameter:

| Operator | Example | Effect |
|----------|---------|--------|
| `""` | `"machine learning"` | Exact phrase match |
| `-` | `python -snake` | Exclude term |
| `OR` | `cat OR dog` | Either term |
| `site:` | `site:github.com react` | Within site |
| `filetype:` | `filetype:pdf report` | File type filter |

## Privacy-Focused Engines

- **DuckDuckGo** — No tracking, built-in Bangs (`!gh`, `!so`, `!w`)
- **Startpage** — Google results + privacy protection
- **Brave** — Independent search index
- **Qwant** — EU GDPR compliant

## Examples

```javascript
// Multi-engine comprehensive search
WebSearch(query="deep learning 2024", region="all", timeFilter="month", topK=3)

// Chinese-only search
WebSearch(query="量子计算最新进展", region="cn")

// Tech documentation
WebSearch(query="React hooks tutorial", site="react.dev")

// Recent news (last 24h)
WebSearch(query="renewable energy", timeFilter="day", region="global")

// WolframAlpha conversion
WebSearch(query="100 EUR to USD")

// Fetch a specific article
WebFetch(url="https://example.com/article")
```
