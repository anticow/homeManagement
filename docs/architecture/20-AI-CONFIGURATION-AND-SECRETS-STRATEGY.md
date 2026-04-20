# 20 - AI Configuration And Secrets Strategy

## Purpose

Define how AI configuration is managed across environments for the HomeManagement + Ollama integration.

This document is Phase 0 scope and should be treated as the source of truth for configuration precedence and secret hygiene.

## Configuration Ownership

- Static defaults: source-controlled appsettings for safe local baseline.
- Environment-specific overrides: environment variables and deployment secrets.
- Production values: secret stores and deployment pipeline injection only.

## Precedence Rule

1. Environment variables
2. appsettings.<Environment>.json
3. appsettings.json

Do not rely on appsettings.json for production secrets.

## AI Configuration Keys

Root section: AI

Key list:
- AI:Enabled
- AI:Provider
- AI:MaxConcurrentRequests
- AI:DefaultTimeoutSeconds
- AI:Ollama:BaseUrl
- AI:Ollama:Model
- AI:Ollama:TimeoutSeconds
- AI:Ollama:NumCtx
- AI:Ollama:MaxTokens
- AI:Ollama:Temperature

Environment variable forms:
- AI__Enabled
- AI__Provider
- AI__MaxConcurrentRequests
- AI__DefaultTimeoutSeconds
- AI__Ollama__BaseUrl
- AI__Ollama__Model
- AI__Ollama__TimeoutSeconds
- AI__Ollama__NumCtx
- AI__Ollama__MaxTokens
- AI__Ollama__Temperature

## Baseline Endpoints

Preferred DNS endpoint:
- http://zombox.cowgomu.net:11434

Fallback endpoint:
- http://192.168.1.8:11434

Use DNS for default configuration, IP only for troubleshooting or DNS outage scenarios.

## Secret Hygiene Rules

1. Never commit API keys, bearer tokens, or private credentials into tracked json/yaml files.
2. Keep AI endpoint and model values configurable, but treat authentication material as secrets.
3. Use per-environment secret injection for any future Ollama auth proxies or gateway keys.
4. Keep automation disabled by default until policy and approval controls are in place.

## CI/CD Requirements

Minimum checks:
- Build must pass with AI disabled defaults.
- Startup validation must fail when AI is enabled and required Ollama settings are invalid.
- Secret scanners should run against repository and pipeline artifacts.

## Operational Guidance

Local development:
- Keep AI disabled unless actively developing AI features.
- Override AI__Enabled and AI__Provider only in local user environment when needed.

Production-like environments:
- Enable AI only with explicit change control.
- Use deployment secrets for endpoint and provider overrides.
- Record model version changes in deployment notes for traceability.
