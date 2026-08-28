# Maintainer notes

Engineering notes for whoever maintains this codebase next. The public
`README.md` at the repo root tells users what they need; this tells an engineer
why the code is shaped the way it is.

## Read in this order

1. [01-architecture.md](01-architecture.md) — what each project does, how data flows, where the boundaries are
2. [02-design-commitments.md](02-design-commitments.md) — rules the codebase follows that no single file makes obvious
3. [03-build-and-release.md](03-build-and-release.md) — `dotnet build`, the tag → `release.yml` flow, signing
4. [04-update-flow.md](04-update-flow.md) — the in-app updater end to end, and what gets verified
5. [05-debugging-playbook.md](05-debugging-playbook.md) — per failure mode: how to read it from the log, what to check next
6. [08-known-limitations.md](08-known-limitations.md) — deferred work, accepted trade-offs, open questions

Gaps in the numbering are intentional. Notes covering particular deployment
environments are kept out of this repository; if you are taking over
maintenance, ask the owner for them.

## Keep them current

These notes are part of the implementation, not a separate artefact. Most of
what they contain is a decision tied to a specific incident — the kind of thing
that is expensive to rediscover and invisible in a diff. When you change the
code, update the page that covers it in the same session.

| If you changed… | Update… |
|---|---|
| project layout, a new or removed project | `01-architecture.md` |
| an architectural rule (env handling, threading, a security boundary) | `02-design-commitments.md` |
| the release workflow, signing, or pinned dependencies | `03-build-and-release.md` |
| the manifest format, Sigstore plumbing, or the version probe | `04-update-flow.md` |
| anything producing a new error category or failure mode | `05-debugging-playbook.md` |
| something you decided **not** to do, or deferred | `08-known-limitations.md` |

## Before you add a file here

This repository is public and git history is permanent — a file committed here
cannot be recalled from forks, clones or archives. `.gitignore` therefore
excludes this directory by default and allow-lists individual files.

Adding a line to that allow-list is a publishing decision. Read the file first,
and confirm it names no organisation, product, host, account, or network
characteristic. Anything else belongs in the private set.
