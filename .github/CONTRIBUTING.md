# Contributing to RetroIO

Thanks for looking. This file says how to get a build running, what the code is expected to look
like, and what will get a change sent back.

RetroIO is the library half of RetroCore Labs: forty-four NuGet packages and no `Main` anywhere in
`src/`. That split is deliberate and it is the first thing to understand before changing anything -
it is what lets a conformance test drive the X.25 packet layer with no sockets at all, and what lets
the same code run inside a desktop application, a Windows service and a console tool.

---

## Getting a build

| # | Requirement | How to check |
|---|---|---|
| 1 | The .NET SDK version pinned in `src/global.json` | `dotnet --info` |
| 2 | PowerShell 7 or newer | `$PSVersionTable.PSVersion` |
| 3 | Nothing else to build the libraries | there are no native dependencies |
| 4 | `src\eng\setup-test-data.ps1` run once | only for the tests that open real disk images |

```powershell
# from the repository root
.\src\build.ps1
```

`build.ps1` builds, runs the tests and publishes the four tools in `src/tools/`. It **stops before
publishing if any test fails**. Useful switches: `-SkipTests` when you know why, `-Clean`,
`-FrameworkDependent` for smaller executables that need .NET installed, `-Runtime linux-x64`.

To hand your changes to the application repositories, pack them into the local folder feed:

```powershell
powershell -File src\eng\publish-local.ps1
```

That writes every package into `../localfeed` as `0.1.0-local.<timestamp>`. The application
repositories reference `0.1.0-local.*`, so a fresh timestamp is always picked up - which is the
point: it is what stops the NuGet cache serving a stale DLL after a change here.

> [!IMPORTANT]
> Pass **`-nodeReuse:false`** on every `dotnet build` and `dotnet test` you run by hand.
> `build.ps1` already does. Node reuse is on by default and leaves MSBuild worker processes behind
> that hold a lock on the output DLL; the next build then quietly keeps the old binary and **the
> tests report green against stale code.** This has actually happened here, which is why it is
> written down.

---

## What the code is expected to look like

 - **No LINQ, and avoid `foreach`.** Use index-based `for` loops. Where a hot path allocates, use
   `Span<T>`, `ArrayPool<T>`, `stackalloc` or a ring buffer. These are protocol and filesystem
   paths; an enumerator allocation per packet or per block is a real cost, not a theoretical one.
 - **Nothing in `src/` gets a `Main`.** If a change needs an entry point, it belongs in `src/tools/` or
   in an application repository.
 - **XML documentation on every public and protected member.** The fix for a missing-comment
   warning is to write the comment - never `<NoWarn>` and never `#pragma warning disable`.
 - **Unit tests only**, in the package's own `tests/` folder, or in `tests/` when the test spans
   several packages. Never add a standalone program that prints something and exits - it cannot
   fail a build.
 - **No FluentAssertions.** Use the test framework's own asserts.
 - **Keep the comments, and add more.** A comment that records a datasheet reference, a
   specification clause, or the reason a thing is NOT done the obvious way is the most valuable
   line in the file. Never delete one because a style guide prefers less; replace one only when it
   has become factually wrong.
 - **SourceLink stays enabled**, through the shared props in `src/eng/build`. Never disable it in an
   individual `.csproj`.
 - **Plain words.** In code, comments, commit messages and documentation alike.
 - **Run `dotnet format`** if whitespace drifts.

### Naming

Package id = root namespace = folder name. `RetroIO.Storage.*` and `RetroIO.Protocols.*` are the
two sides; nothing sits directly under `RetroIO.` unless it belongs to neither. The standard is
`NAMING.md`, kept beside the repositories. The organisation is **RetroCoreLabs**; the old name
`HackerCorpLabs` is retired and must not appear in anything new.

---

## Documentation changes

```powershell
python src\eng\check-docs.py
```

It must exit 0. It fails on a relative link that does not resolve, a relative link that escapes the
repository root, a machine-specific absolute path, and a Mermaid diagram that is malformed or
breaks `MERMAID_COLOR_STANDARDS.md`.

> [!WARNING]
> **Never put an absolute path in anything committed here.** A drive letter or a home directory is
> correct on exactly one machine, and this repository is public. A path inside the repository goes
> repo-relative; a sibling repository is named, not located; anything outside is described. A
> variable such as `%USERPROFILE%` or `~` is fine - that is the portable way to name a per-user
> location.

Every package has its own README. It follows `README-TEMPLATE.md`, beside the repositories: one
sentence at the top that a stranger to the subject understands, a code example early, install
before build-from-source, licence last.

### The documentation site

The site at <https://retrocorelabs.github.io/RetroIO/> is built by DocFX on every push to `main`: the API reference
comes from the XML comments, and the READMEs and guides come in as pages. The menu of those pages is generated from the
files git tracks. After adding a package, a tool or a guide, from the repository root:

```powershell
python src\docfx\gen_toc.py            # rewrite the menu; commit the result
cd src; docfx docfx\docfx.json --warningsAsErrors   # must end with 0 warnings and 0 errors
```

The workflow fails when the menu is out of date or when the build has a warning. `src\docfx\README.md` says more.

---

## Submitting a change

 1. Branch off `main`. Do not commit to `main` directly.
 2. One logical change per commit. A commit message says **what changed and why**, in plain words -
    the subject line in the imperative, then a blank line, then the reasoning. If you measured
    something, put the number in the message.
 3. `.\src\build.ps1` passes, including the tests, before you open the pull request.
 4. Say in the pull request what you verified and how. "Tests pass" on its own is not useful; "the
    three LAPB timer tests now cover the T2 case, 42 tests, all green" is.
 5. A change to a wire format or an on-disk format needs a test that reads a real capture or a real
    image, not a hand-written buffer that agrees with the new code.
 6. If something is still broken or unfinished, say so plainly. A known gap that is written down is
    fine. One that is hidden is not.

---

## Licence

By contributing you agree your work is licensed under the MIT licence in [LICENSE](../LICENSE).
