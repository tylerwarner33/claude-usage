# Claude Usage

A command-line tool and local web dashboard for Claude Code token usage and cost.
It reads the JSONL transcripts that Claude Code writes, stores the results in SQLite, and shows them as console tables or a dashboard page.
See `README.md` for setup and commands.

These are the instructions for a coding agent that works on this repository (ex. Claude Code, GitHub Copilot).

## Layout

- `Source/ClaudeUsage/` is the only product project. It builds the `claude-usage` executable.
- `Tests/ClaudeUsage.Tests/` holds the xUnit tests.
- `Directory.Build.props` sets the shared build properties. `Directory.Packages.props` holds every package version.

Each folder in `Source/ClaudeUsage/` is one feature, and the code for that feature stays inside it:

| Folder | Feature |
| --- | --- |
| `Scanning/` | Parse transcripts and scan them into the database |
| `Storage/` | Open the SQLite database and create its schema |
| `Pricing/` | Price table and cost calculation |
| `Reports/` | `today`, `week` and `stats` console tables |
| `Dashboard/` | Web server and `/api/data` queries |

`Program.cs` only parses arguments and calls a feature. Keep logic out of it.

## Working Principles

- **Add a feature in its own folder.** Do not spread one feature over several folders. Share code between features only when two features need the same code.
- **Keep `Pricing/ModelPricing.cs` the only price table.** The dashboard page receives its prices from the server.
- **Keep the executable self-contained.** The dashboard page and icon are embedded resources, so a single-file publish stays one file.
- **Keep evidence out of code comments.** A remark says what a change to the code needs. Dates, measurements and what was tried go in `README.md` or a doc.

## Code Conventions

The `.editorconfig` enforces most of these rules, and the build fails on a breach.

- Use tabs with a width of 3.
- Do not use `var`. Write the type (ex. `HttpClient client = new();`).
- Use file-scoped namespaces. The namespace matches the folder.
- Name private and internal fields `_camelCase`. Name constants in PascalCase. Start interface names with `I`.
- Use expression bodies for properties and accessors. Do not use them for methods and constructors.
- Use primary constructors where they fit.
- Write the accessibility modifier on every non-interface member.
- Use full names, not abbreviations (ex. `personalAccessToken`, not `pat`).
- Write `x is not null` and `x is false`, not `x != null` and `!x`.
- Put namespaces that three or more files use in `Source/ClaudeUsage/GlobalUsings.cs`. Put the rest in the file.
- Write each XML doc tag on its own line. Indent its content one tab. Write one sentence per line.
- Use `-` and not an em dash. Use `ex.` and not `e.g.`.
- Add a package version to `Directory.Packages.props` only. A `PackageReference` has no `Version`.

## Validation

Run these from the repository root before you finish:

```
dotnet build ClaudeUsage.slnx
dotnet test ClaudeUsage.slnx
```

The build must show 0 warnings, because `EnforceCodeStyleInBuild` is on.
Do not run the dashboard from `Source/ClaudeUsage/bin`. A running executable locks the folder and blocks a rename or a rebuild.
