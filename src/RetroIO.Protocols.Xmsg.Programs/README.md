# RetroIO.Protocols.Xmsg.Programs

## What it is

The toolkit every XMSG program shares, whether it is a server, a client or a manager ("-mon") program. Each
program has its own commands; this package is the machinery around them. The first part is the command framework:

- `CommandTable`: register commands, then run typed lines against them;
- `CommandDefinition` and `CommandParameter`: a command's name, description, parameters (required or optional,
  each with a description) and the function that runs it;
- `CommandResult`: zero or more output lines (`Text`, `Warning`, `Error`), so every front end shows the same thing.

## Why you need it

A chat client's `/join`, a manager's `LIST-TRUNKS` and a server's startup file are all "a command with
parameters". Writing the parsing, the help and the error messages once means every program behaves the same.

## What you can use it for

```csharp
CommandTable table = new CommandTable("CHAT-MON commands:");
table.Register(
    "INITIALIZE",
    "how much that room keeps",
    c => c.Write(c.Get("room") + " keeps " + c.Get("n")),
    new CommandParameter("room", "the room", required: true),
    new CommandParameter("n", "how many lines it keeps", required: true));

CommandResult result = table.Execute("ini lobby 50");   // "lobby keeps 50"
```

## Behaviour worth knowing

- **SINTRAN's abbreviation rule.** Every part must be given and each typed part must start the real one:
  `LIST-TRUNKS` answers to `L-T`, `LI-TRU`, `LIST-T`, not to `L`. Any letter case. An abbreviation that fits two
  commands is refused and both are listed. The rule is CHAT-MON's own (CHATMON.PLNC).
- **Spaces or commas separate parameters**; an empty place between commas means "not given": `CMD a,,c`.
- **Nothing is asked for.** A missing required parameter fails the command and names every missing one with what it
  is, followed by the usage line.
- **`HELP` and `?` are built in**, generated from the registrations; `HELP <command>` describes one command.
- **A prefix for slash commands**: `new CommandTable("Chat commands:", "/")`; `IsCommand` tells a command from
  chat text.
- **A parameter can take the rest of the line** (`takesRestOfLine`), for the text of `/tell NAME text`.
- **An exception in a command's function is not caught**; a defect must show.

## Prerequisites

None. No XMSG connection is needed for the command framework.

## Related

- `RetroIO.Protocols.Xmsg.Plugins.Client`: the connection to RetroServer.
- `docfx/xmsg/`: the developer documents for XMSG programs.
