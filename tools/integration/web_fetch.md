# tool: web_fetch
domain: integration
type: http
description: Download a URL and return its visible text content. HTML pages get scripts, styles, and navigation stripped. Truncated at ~50K chars.

## parameters
- url: string (required) — Absolute http:// or https:// URL
- user_agent: string (default: "LTAI-Agent/1.0") — User-Agent header

## http
method: GET
url: "{{url}}"
headers:
  User-Agent: "{{user_agent}}"
  Accept: "text/html, text/plain, application/json"
timeout: 30
max_output: 50000

## triggers
- pattern: "fetch url" (weight: 1.0)
- pattern: "web fetch" (weight: 0.9)
- pattern: "download page" (weight: 0.8)
- pattern: "抓取网页" (weight: 0.7)
- pattern: "get url content" (weight: 0.7)
- pattern: "read webpage" (weight: 0.6)

## tags
- integration
- safe
- web
