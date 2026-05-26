# tool: notify
domain: integration
type: service
description: Send notification via configured channel

## parameters
- channel: string (required) — Notification channel
- to: string (required) — Recipient identifier
- message: string (required) — Notification message

## service
name: MessageGateway
method: SendAsync

## triggers
- pattern: "notify" (weight: 1.0)
- pattern: "通知" (weight: 0.9)

## tags
- integration
- modify
