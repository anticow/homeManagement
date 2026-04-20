# 19 - Local LLM Integration ADR

## Status

ACCEPTED (2026-04-04)

## Context

HomeManagement is being extended with OpenClaw-style workflow automation and LLM-assisted planning/summarization.

Requirements for this decision:
- LLM processing must remain local/private.
- Existing HomeManagement modules should be reused instead of introducing a disconnected control plane.
- Phase 0 must keep runtime safe by default and feature-gated.

Known infrastructure constraints:
- Local LLM host is reachable as zombox.cowgomu.net or 192.168.1.8.
- Target model family is Qwen 2.5 on Ollama.
- Initial GPU host profile is GeForce RTX 3080 12GB.

## Decision

1. HomeManagement remains the authoritative control plane.
- New AI and automation capabilities are integrated as modules in the existing .NET solution.

2. Local inference provider is Ollama.
- Initial endpoint default: http://zombox.cowgomu.net:11434.
- IP fallback endpoint: http://192.168.1.8:11434.

3. Initial model baseline is quantized Qwen 2.5 7B.
- Default model identifier: qwen2.5:7b-instruct-q4_K_M.

4. AI and automation are disabled by default in Phase 0.
- Runtime activation is explicitly config-gated.
- Startup validation is fail-fast for invalid enabled configuration.

5. Phase 0 scope excludes autonomous mutating execution.
- Natural-language-to-action paths remain out of scope until policy guardrails and approval workflows are delivered.

## Consequences

Positive:
- Preserves existing inventory, auth, transport, scheduling, and audit boundaries.
- Keeps sensitive workloads local and private.
- Provides a clear and testable feature-flag path for incremental rollout.

Trade-offs:
- Broker availability now depends on AI option correctness when AI is enabled.
- GPU host availability and local network reliability become operational dependencies for AI features.

Deferred concerns:
- Security hardening review for prompt-injection resistance, command allowlists, and model-output risk control is tracked as deferred work in the integration plan.

## Implementation Notes (Phase 0)

- AI contracts: src/HomeManagement.AI.Abstractions.
- Ollama adapter: src/HomeManagement.AI.Ollama.
- Automation skeleton: src/HomeManagement.Automation.
- Broker options binding and validation: src/HomeManagement.Broker.Host/Program.cs.

## Exit Criteria For This ADR

- Solution builds with AI modules included.
- Broker starts with AI disabled by default.
- Enabling AI with invalid Ollama configuration fails at startup with explicit validation errors.
