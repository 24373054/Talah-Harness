# Rightsholder authorization record

This repository record documents the authorization supplied for the creation
and distribution of Talah Harness. It is a provenance record, not a substitute
for a signed legal instrument or independent legal advice.

## Grant recorded on 2026-08-13

The GitHub owner `24373054`, acting for the same rightsholder that controls TLAH
Studio and Talah Harness, expressly authorized the Talah Harness project to:

- use and adapt TLAH Studio source code and its existing Windows desktop shell;
- reuse, replace, edit, and create derivative UI and visual assets;
- modify the authorized materials and combine them with other software;
- publish the resulting Talah Harness source repository; and
- distribute and commercially exploit Talah Harness and its authorized
  derivatives.

The authorization covers the TLAH Studio source dependency recorded at
`third_party/TLAH-Studio`, including the release baseline pinned to commit
`3ff42e06dca0359ce499c490cc7879934d439150`. It does not change the license of
TLAH Studio for unrelated recipients or unrelated projects, and it does not
relicense third-party components.

## Repository boundary

Talah Harness development must not rewrite or silently modify the original
TLAH Studio repository. Harness-specific changes live in this parent repository
or, if a future shell fork is required, in a separately named authorized fork.
The commit-pinned submodule remains the auditable upstream source boundary.

## Release attestation

For a production release, the rightsholder should countersign this authorization
outside the repository or cryptographically sign the source tag and GitHub
Release that refer to it. Release provenance, the submodule commit, SBOM,
notices, and this record are packaged together and covered by `SHA256SUMS`.
