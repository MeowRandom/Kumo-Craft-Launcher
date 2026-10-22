# Contributing to Kumo

## Development setup

Requirements:

- Windows 10 or newer
- .NET 9 SDK
- Java 17 or newer for running the smoke tests

Restore and build:

```powershell
dotnet restore .\Kumo.csproj --configfile .\NuGet.Config
dotnet build .\Kumo.csproj -c Release --no-restore
```

Run the tests:

```powershell
dotnet run --project .\tests\Kumo.SmokeTests\Kumo.SmokeTests.csproj `
  -c Release --configfile .\tests\Kumo.SmokeTests\NuGet.Config
```

## Pull requests

- Keep changes focused on one feature or bug.
- Follow the existing WPF component and service boundaries.
- Do not commit account tokens, player data, game assets, or generated build output.
- Include a short description of user-visible behavior and test coverage.
- Run a Release build and the smoke tests before opening a pull request.

## Commit messages

Use short imperative messages, for example:

- `Add installed-version repair action`
- `Fix Microsoft device login cancellation`
- `Cache WebP mod covers as display PNGs`

## Licensing

Contributions are accepted under the MIT License.
