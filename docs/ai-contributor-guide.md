# AI Contributor Guide

This document gives AI coding agents and contributors project-specific context.

## Important areas

### Runtime

The runtime is the core agent execution layer. It should remain lightweight and as trim-safe as possible.

### Gateway

The gateway owns HTTP, WebSocket, webhooks, auth, rate limiting, and observability concerns.

### Plugin compatibility

Compatibility targets the mainstream tool-plugin path, not full upstream extension-host parity.
Unsupported surfaces should fail fast with diagnostics.

### Semantic Kernel

SK interop is optional and should remain isolated from the core NativeAOT-friendly runtime path.

### Experimental branches

Experimental branches may include JIT-only or MAF-specific work that should not automatically be treated as the default architecture.

## Preferred AI contribution areas

- tests
- compatibility matrix updates
- docs improvements
- refactors that reduce startup and composition complexity
- observability improvements

## Areas that require caution

- public-bind security behavior
- plugin compatibility claims
- NativeAOT-sensitive code paths
- new mandatory dependencies
