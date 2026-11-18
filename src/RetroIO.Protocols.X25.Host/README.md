# RetroIO.Protocols.X25.Host

Scripted X.25 hosts. A `ScriptedHost` runs one JavaScript program per fake machine: the program gets an event
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
