# Contributing

Thanks for helping improve .NET Real-Time Speech Lab.

## Local checks

Run these commands from the repository root before opening a pull request:

```powershell
dotnet build
dotnet run -- --self-test
```

Live provider checks require your own API credentials, may incur charges, and should never be run with credentials stored in source files or committed configuration.

## Pull requests

- Keep provider-specific changes isolated where possible.
- Include deterministic coverage for protocol or lifecycle behavior.
- Document provider assumptions and any live-only validation.
- Do not include API keys, recordings containing private speech, or generated `bin`/`obj` output.