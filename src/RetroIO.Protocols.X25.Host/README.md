# RetroIO.Protocols.X25.Host

Fake X.25 machines, of two kinds. Both implement `IHostMachine`, so one program can run either kind:

- **`DescribedHost`** runs a host folder: a `host.ini` and text files that say what to print and which typed
  words lead where (a banner, a login, a command line or menus, a fake file tree, `who`, `send`, `wall`, chat
  rooms). Nothing in the folder runs. `HostDescription.Load(folder, hostname, address)` reads and checks the
  folder; every mistake stops it with the file, the line and the name. The format is
  `src/docs/x25host-format.md` in the RetroX25 repository. To hide a password the host has to tell the caller's
  PAD to stop echoing (an X.29 Set), so its output must also implement `IHostPadControl`.
- **`ScriptedHost`** runs a JavaScript program, described below.

`Admit(callUserData)` is asked before a call is accepted. A `DescribedHost` refuses a call when it already has
`sessions` calls, or (with `pad_calls_only`) when the call user data does not start with the X.29 protocol
identifier 01 00 00 00. A `ScriptedHost` takes every call.

## Scripted hosts

A `ScriptedHost` runs one JavaScript program per fake machine: the program gets an event
for each call (`onConnect`, `onInput`, `onDisconnect`) and keeps whatever it needs in its own variables, so every
caller on the same machine shares it. That is enough for a chat, `WHO`, `WALL` and messages between users.

The host has no I/O, clock or threads. Its caller feeds it calls, input and the time, and gets sends, clears
and log lines back through `IHostOutput`. The X.25 side is the caller's job, which is how `AltosChatEngine` is
fed as well.

## The script's view

| Name | What it is |
|---|---|
| `onStart()` | Runs once when the machine starts |
| `onConnect(s)` | A call arrived; `s` is its session |
| `onInput(s, text)` | Bytes from the caller, one character per byte, exactly as they came |
| `onDisconnect(s)` | The caller's call is gone (sends to `s` are dropped) |
| `s.id`, `s.login`, `s.calling`, `s.connectedAt` | The call |
| `s.send(text)` | Sends text; `\n` becomes the line end |
| `s.clear()` | Clears the call |
| `s.pending()` | Bytes sent and not yet delivered |
| `s.isOpen()` | Whether the call is still there |
| `machine.settings` | The machine's settings, every value a string |
| `machine.log(text)` | A log line |
| `machine.now()` | `{year, month, day, hour, minute, second, millisecond, weekday, ticks}`, the clock the host was given |
| `machine.after(ms, fn)` | Run `fn` at the first tick `ms` from now; returns an id for `machine.cancel(id)` |
| `machine.wireLength(text)` | The bytes `text` takes on the wire |
| `machine.session(id)`, `machine.sessions()` | One open session, or all of them in the order they came |
| `machine.broadcast(text, exceptId)` | `send` to every open session but one |

## Safety

The script runs in Jint with CLR access off, so it cannot reach a .NET type, a file, a socket or a process. It
can only use the functions in the table. Every call into the script runs under limits on memory, time,
statements and recursion (`ScriptedHostSettings`).

What happens when a script fails:

- **It does not load:** the constructor throws, so the machine does not start.
- **It fails while serving a call:** that call is cleared and the failure is logged.
- **It fails in `onStart` or in a timer:** the failure is logged.

## Example

`scripts/altos-chat.js` is the Altos CHAT 3.1 conference written against this API only. A test runs it and the
C# `AltosChatEngine` side by side and checks they produce the same bytes, clears and log lines.
