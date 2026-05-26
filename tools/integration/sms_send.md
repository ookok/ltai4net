# tool: sms_send
domain: integration
type: service
description: Send SMS text message

## parameters
- message: string (required) — SMS message content
- phone: string (required) — Recipient phone number

## service
name: SmsGateway
method: SendAsync

## triggers
- pattern: "send sms" (weight: 1.0)
- pattern: "发送短信" (weight: 0.9)

## tags
- integration
- modify
