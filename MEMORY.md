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

Run `memory where` to see which folder and machine you are on.
