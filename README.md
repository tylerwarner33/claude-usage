# Claude Code Usage (C#)

A local dashboard and CLI that show your Claude Code token usage and estimated API cost, by model, day, project and session.

It is a C# (.NET 10) application: a scanner, a SQLite database, console reports and a web server.

## What it reads

Claude Code writes one JSONL transcript per session to `~/.claude/projects/`.
Each `assistant` record contains the model id and the token usage of one API response.
This tool reads those files and stores the data in a SQLite database.
It does not change the transcripts, and it does not send data to a network.

The tool records:

- Claude Code CLI sessions
- Claude Code sessions in VS Code and other IDEs
- Subagent turns (ex. `Explore`, `general-purpose`, `fork`), with their types

The tool cannot record:

- Sessions on a different computer
- Cowork sessions and claude.ai chats (they write no local transcripts)

## Requirements

- .NET 10 SDK to build. A self-contained publish needs no runtime on the target computer.

## Quick start

```
dotnet run --project src/ClaudeUsage -- dashboard
```

This command starts the dashboard at http://localhost:8080, opens your browser and scans in the background.

## Commands

```
claude-usage scan [--projects-dir PATH]   Scan JSONL files and update the database
claude-usage today                        Show today's usage by model
claude-usage week                         Show the last 7 days (per day + by model)
claude-usage stats                        Show all-time statistics
claude-usage dashboard [--projects-dir PATH] [--host HOST] [--port PORT] [--no-browser]
claude-usage --version
```

With `dotnet run`, put `--` before the command (ex. `dotnet run --project src/ClaudeUsage -- week`).

The `today`, `week` and `stats` tables show the estimated cost and the cost share of each model.

## Install as one executable

```
dotnet publish src/ClaudeUsage -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Then put `publish\claude-usage.exe` in a folder on your `PATH`.

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `CLAUDE_USAGE_DB` | `~/.claude/claude-usage.db` | Database file |
| `CLAUDE_CONFIG_DIR` | `~/.claude` | Claude Code configuration directory |
| `HOST`, `PORT` | `localhost`, `8080` | Dashboard address (`--host` and `--port` have priority) |
| `~/.claude/claude-usage-pricing.json` | (no file) | Adds or replaces model prices |

Example pricing override file (prices in US dollars per million tokens):

```json
{
  "claude-opus-6": { "input": 4.00, "output": 20.00, "cache_write": 5.00, "cache_read": 0.20 }
}
```

## Cost estimates

Costs use Anthropic API prices from [claude.com/pricing](https://claude.com/pricing#api), checked October 2026.
If you use a Pro or Max subscription, these numbers are not what you pay.
They show the value of your usage at API prices.

| Model | Input | Output | Cache write | Cache read |
|---|---|---|---|---|
| Fable 5.1 | $10.00 | $50.00 | $12.50 | $0.25 |
| Fable 5, Mythos 5 | $10.00 | $50.00 | $12.50 | $1.00 |
| Opus 5.5 | $4.00 | $20.00 | $5.00 | $0.20 |
| Opus 5, Opus 4.5 to 4.8 | $5.00 | $25.00 | $6.25 | $0.50 |
| Opus 4.1 and older | $15.00 | $75.00 | $18.75 | $1.50 |
| Sonnet 5.5, Sonnet 5 | $2.00 | $10.00 | $2.50 | $0.20 |
| Sonnet 4.x, Sonnet 3.7 | $3.00 | $15.00 | $3.75 | $0.30 |
| Haiku 4.5 to 4.7 | $1.00 | $5.00 | $1.25 | $0.10 |

The tool finds a price by exact model id, then by the longest id prefix (ex. `claude-haiku-4-5-20251001`).
An unknown id that contains a family name (ex. `opus`) gets the price of the current model of that family.
Models that do not contain `fable`, `mythos`, `opus`, `sonnet` or `haiku` get no cost.
`src/ClaudeUsage/Pricing/ModelPricing.cs` is the only price table. The server sends it to the dashboard page.

## Behavior notes

- Days are local calendar days, not UTC days. "Today" agrees with the clock on your computer.
- The scanner stores a byte offset for each file, not a line count. A half-written last line is read on the next scan, not lost.
- The scanner reads `subagents/agent-*.meta.json`. Background subagents then show their type, not `unknown`.
- Only one scan runs at a time. The background scan and the Rescan button cannot write at the same time.

## Project layout

| Path | Purpose |
|---|---|
| `src/ClaudeUsage/Program.cs` | Command-line entry point |
| `src/ClaudeUsage/Scanning/` | JSONL parser and incremental scanner |
| `src/ClaudeUsage/Storage/UsageDatabase.cs` | SQLite schema and connections |
| `src/ClaudeUsage/Pricing/ModelPricing.cs` | Price table and cost calculation |
| `src/ClaudeUsage/Reports/ConsoleReports.cs` | `today`, `week` and `stats` tables |
| `src/ClaudeUsage/Dashboard/` | Web server and `/api/data` queries |
| `src/ClaudeUsage/wwwroot/index.html` | Dashboard page (embedded in the executable) |
| `tests/ClaudeUsage.Tests/` | xUnit tests |

## Tests

```
dotnet test
```
