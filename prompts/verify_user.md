# prompt: verify_user
domain: verification
description: Factuality verification user prompt with tool results and model answer
## triggers
verify user, factuality user, verify

## template
Tool results:
---
{context}
---

Model answer:
---
{response}
---

Is every factual claim in the model answer directly supported by the tool results?
Answer ONLY: YES or NO, followed by a single short reason.
