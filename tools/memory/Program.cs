// memory
//
// The shell's way into the memory. The command itself - remember, recall, correct,
// learn, list, show, sync, where - is CircleAI.Memory.Cli.MemoryCli, inside the
// CircleAI package, because Circle AI ships as ONE package. This project is only what
// AGENT.md's two install lines pack into a local dotnet tool, so "memory" is on PATH.

return await CircleAI.Memory.Cli.MemoryCli.RunAsync(args);
