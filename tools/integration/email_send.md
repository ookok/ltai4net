# tool: email_send
domain: integration
type: service
description: Send email via SMTP

## parameters
- to: string (required) — Recipient email address
- subject: string (required) — Email subject line
- body: string (required) — Email body content

## service
name: MessageGateway
method: SendSmtpAsync

## triggers
- pattern: "send email" (weight: 1.0)
- pattern: "发送邮件" (weight: 0.9)

## tags
- integration
- modify
