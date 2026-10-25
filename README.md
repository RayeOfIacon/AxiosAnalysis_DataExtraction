# Axios Project Health Data Collectors

This .NET console project collects GitHub and local Git data for a course project health analysis of [`axios/axios`](https://github.com/axios/axios). It produces raw CSV files for subsequent analysis; it does not calculate project health scores or classify contributors automatically.

## Observation windows

All timestamps use UTC. The date boundaries below are inclusive.

| Window | Start | End | Used by |
| --- | --- | --- | --- |
| Current | 2025-09-01 00:00:00 | 2026-08-31 23:59:59 | Current-year collectors |
| Pre-current five years | 2020-09-01 00:00:00 | 2025-08-31 23:59:59 | Contributor history collector |

The five-year history ends immediately before the Current window and does not include any Current-window activity.

## Requirements and setup

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git, for the collectors that read local commit history
- A local clone of [`axios/axios`](https://github.com/axios/axios), with sufficient history for the observation windows
- A GitHub personal access token available as the `GITHUB_TOKEN` environment variable

From PowerShell in this project directory:

```powershell
$env:GITHUB_TOKEN = "<your-token>"
$env:AXIOS_REPO_PATH = "C:\path\to\your\axios\clone"
dotnet build
dotnet run -- help
```

Use your actual Axios clone path for `AXIOS_REPO_PATH`. The `actors` and `history5y` commands require it; the other commands use the GitHub API. Do not save the token in source code, a committed settings file, or this README.

### Data and repository paths

The original workspace layout is:

```text
cap_final/
├── Data/
│   ├── AxiosHealthAnalysis/   # This project
│   └── Extracted_data/        # Generated CSV and checkpoint files
└── Repo/
    └── axios_repository/      # Local Axios Git clone
```

`AnalysisConfig.cs` currently resolves the output directory relative to the compiled executable. In the original layout, it writes to `Data/Extracted_data`, creating that directory if needed. If this project is cloned as a standalone repository, the default output directory becomes an `Extracted_data` directory beside the cloned project folder, not inside it. Set `AXIOS_REPO_PATH` to the actual Axios clone location in a standalone setup. There is currently no environment-variable override for the output directory.

The generated CSV files and the local Axios clone are outside this repository and are not included when only the `AxiosHealthAnalysis` folder is uploaded.

## Collectors and recommended order

Run each command from the directory containing `AxiosHealthAnalysis.csproj`:

| Order | Command | Main output | Purpose |
| --- | --- | --- | --- |
| 1 | `dotnet run -- pulls` | `pull_requests_current.csv` | Current-window PR authors, lifecycle, status, labels, reviewers, and change size. |
| 2 | `dotnet run -- issues` | `issues_current.csv` | Current-window Issue authors, lifecycle, labels, assignees, and comment counts. |
| 3 | `dotnet run -- interactions` | `pr_reviews_current.csv`, `pr_comments_current.csv`, `issue_comments_current.csv` | Reviews, PR conversation comments, inline review comments, and Issue comments in the Current window. Requires `pull_requests_current.csv`. |
| 4 | `dotnet run -- activities` | `github_activity_events.csv` | Compact Current-window table of commits, PRs, and Issues. The current Python analysis reads its Commit rows. |
| 5 | `dotnet run -- actors` | `actors.csv`, `commit_identities_current.csv` | Account profiles, preliminary bot signals, and raw/canonical current-window Git identities. Reads existing current-window collector outputs to discover actors. Requires the Axios clone. |
| 6 | `dotnet run -- history5y` | `contributor_activity_pre_current_5y.csv` | Event-level contributor activity in the five years before the Current window. Requires the Axios clone. |

The `interactions` collector also writes `interactions_checkpoint.json`; the five-year history collector writes `contributor_history_5y_checkpoint.json`. These files support resuming long runs. Keep them alongside the corresponding CSV files when resuming a collection. The other collectors check existing records to avoid duplicate output on reruns. The `activities` command rewrites its compact CSV.

Earlier versions also produced `pull_requests_previous.csv`, `actor_activity_history.csv`, and `commit_identities_previous.csv`. The current collectors no longer generate these three files because the current Python analysis does not read them. Any existing copies remain untouched.

GitHub API collection may take a long time and may pause when the authenticated rate limit runs low. The `history5y` review stage requests reviews separately for historical PRs and can require many API calls.

## Interpretation and limitations

- `contributor_activity_pre_current_5y.csv` has one row per observed event, not one row per contributor. Aggregate and resolve identities during analysis.
- The five-year collector records local Git commits reachable from the default remote branch. It is not an exhaustive record of commits on every branch or fork.
- Historical reviews are requested for PRs created within the five-year window. A review during that window on an older PR may be absent. Repository-wide comment endpoints are queried separately.
- A first observed activity in this dataset does not prove a contributor's first-ever activity in Axios. Describe the observation window and identity-matching rules when reporting newcomer or dormant counts.
- `IsBotByType` and `IsBotByLogin` in `actors.csv` are preliminary indicators. Borderline bot and shared-account cases require review of additional evidence.
- PRs and Issues are assigned to observation windows by creation time. Their later status fields may reflect the state at collection time; apply a reporting cutoff if the analysis requires a fixed end-of-window state.

The CSVs are source data for the course report. Sampling decisions, contributor categories, and project health interpretations should be documented in the downstream analysis rather than inferred from a single CSV field.
