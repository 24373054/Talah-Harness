# ADR 0004: TLAH Studio remains a read-only submodule

- Status: Accepted
- Date: 2026-08-13

## Decision

`third_party/TLAH-Studio` points to the original repository at the exact green
commit `3ff42e06dca0359ce499c490cc7879934d439150`. Talah Harness never commits into,
force-pushes, or rewrites that repository. The common rightsholder has authorized
reuse, modification, public distribution, commercial use, and visual-asset use
for this product.

All Harness-specific UI, release identity, and adapter code lives in the parent
repository. If an upstream source change later becomes unavoidable, it must be
made in a separately named authorized derivative repository and reviewed before
the submodule URL is changed.

