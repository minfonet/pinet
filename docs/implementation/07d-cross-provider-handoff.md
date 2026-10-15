# Stage 7d - Cross-provider handoff (Implementation Notes)
- /model with no argument lists models grouped by provider
- /model provider/id switches models; RebuildAgent preserves current messages
- ConvertToLlm path includes normalization scaffold (preserves text/tool calls/thinking)
