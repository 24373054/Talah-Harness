# Design system: Trace Ledger

## Subject and audience

Talah Harness is a Windows coding-agent control room for developers supervising
long-running, tool-using sessions. Its main surface is an operational ledger,
not a consumer chat screen.

## Visual tokens

The palette inherits the KeEntropy identity while removing the previous generic
purple-neon treatment.

| Token | Value | Use |
|---|---:|---|
| Ke Navy | `#080645` | Deep structural surfaces and title bar |
| Ledger Blue | `#115A82` | Selected navigation and information state |
| Chain Cyan | `#62C7E5` | Live trace, focus, links, Codex activity |
| Evidence Gold | `#D8B956` | Decisions, approvals, attention |
| Graphite Ink | `#162230` | Dark content canvas and high-contrast text |
| Mist White | `#F4F7FA` | Light canvas and dark-theme primary text |

Status colors are semantic and never used as decoration: verified green
`#3FA77C`, destructive red `#D9514E`, and warning amber `#D99A45`.

## Type

- Display and session titles: **Bahnschrift SemiCondensed**, used sparingly for
  an instrument-panel character.
- Body and controls: **Segoe UI Variable Text**, optimized for Windows reading.
- Paths, commands, protocol data, and usage: **Cascadia Mono**.

## Layout

```text
┌──────────────────────────────────────────────────────────────────────┐
│ product / workspace       CODEX ●   OPENCODE ●   TLAH ●    account │
├───────────────┬──────────────────────────────────┬───────────────────┤
│ SESSION       │ TRACE LEDGER                     │ INSPECTOR         │
│ LEDGER        │                                  │ capabilities      │
│               │  ○ prompt                       │ permissions       │
│ grouped by    │  │                               │ files / diff      │
│ workspace     │  ● tool / command               │ diagnostics       │
│ and kernel    │  │                               │                   │
│               │  ◆ decision                     │                   │
│               │  │                               │                   │
│               │  ● result                       │                   │
├───────────────┴──────────────────────────────────┴───────────────────┤
│ kernel-aware composer                         context / send / stop │
└──────────────────────────────────────────────────────────────────────┘
```

The center is a full-width reading surface. User prompts are compact command
entries; agent output is not placed into alternating chat bubbles.

## Signature: the Trace Rail

A single vertical rail links prompts, reasoning, tools, approvals, file changes,
and results. Node shape encodes item kind; color encodes state or selected
kernel; motion only communicates live progress. It is both the memorable visual
element and the primary navigation model for an agent run.

The rail derives from the KeEntropy `TRACE` vocabulary and entropy-field mark,
so it is specific to this product rather than generic AI decoration.

## Self-critique and revision

The first direction used a dark canvas, glow, and floating glass cards. That was
too close to the generic AI-product default and the existing low-fidelity TLAH
marketing render. The revised system spends its visual risk on the Trace Rail,
keeps surfaces quiet and squared, limits glow to a running node, and uses the
entropy field only in onboarding and empty states.

Both light and dark themes are first-class. Keyboard focus, high contrast,
screen-reader names, 200% text scaling, and reduced motion are release gates.

