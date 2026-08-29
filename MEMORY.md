# There is a memory. Use it.

It is local, it is a text file, and it already holds what this machine and the
other two worked out. Nothing leaves the machine.

**Before doing something that might have been done before — ask it.**

```bash
memory recall --doing deploy --to android --brief
```

**When something is decided, resolved, or turns out not to work — tell it.**

```bash
memory remember "Use -t:InstallKeepingData when iterating" \
       --about deploy:android \
       --challenge "-t:Install wiped 817 MB of models on every deploy"

memory correct <id> "<what it should have said>"
memory failed  <id> "<what went wrong>"
```

**Or let it read the conversation and keep what is worth keeping.**

```bash
memory learn --about deploy:android          # reads stdin: what the person said
```

On this machine a `UserPromptSubmit` hook already runs `memory learn --hook` on
every prompt, so rules stated in conversation are captured without anybody
running anything. It never blocks and never prints. Recall is still yours to ask
for.

That is the whole of it. `memory help` has the rest; [AGENT.md](AGENT.md) has
the reasoning.

---

**It answers; it does not stop you.** An empty recall is an answer and exits
zero. Nothing here blocks a command, gates a decision, or tells you what you
may do. Recall, not handcuffs — a memory that argues gets turned off, and a
memory that is off remembers nothing.

**It costs one command.** Recall replays the logs each run, so it is never
stale after a `git pull`, and `--brief` fits inside a prompt.

**Ask even when you think you know.** The whole cost of a repeated mistake is
paid before anybody remembers making it the first time. That is what this is
for.

## This repo carries its own

Clone it and you inherit what it knows — including sixty-nine skills that live
outside it:

```bash
export CIRCLEAI_MEMORY=./memory
memory recall "accessibility screen reader touch targets" --brief
```
```
- fact: Skill 'accessibility-inclusive-design' — WCAG, keyboard/screen-reader
  usability, reduced motion, contrast, assistive technology...
```

That is the point of it being here rather than in a README. A pointer in a
document is read once at the top of a session and forgotten by the time it
matters; an atom filed under the situation arrives when somebody is about to do
the thing. Every one of those skills was on this machine all week and none of
them was opened, while the mistakes they describe were made instead.

Two memories, two scopes. `./memory` travels with the code and holds what is
true about the project. `~/.circleai/memory` is yours and follows you across
machines. Neither is a copy of the other.

Run `memory where` to see which folder and machine you are on.
