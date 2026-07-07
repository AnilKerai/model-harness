# Contributing

Thanks for your interest in the Model Harness. This is a small, focused .NET library — contributions that keep it that way are very welcome.

## Build and test

```bash
dotnet build SapphireGuard.ModelHarness.slnx
dotnet test tests/SapphireGuard.ModelHarness.Framework.Tests.Unit/
```

Warnings are errors (`TreatWarningsAsErrors`), so a clean build is a passing build. To sanity-check a change end to end, run a sample — samples fall back to a `FakeModelClient` when no API key is present, so you don't need one:

```bash
dotnet run --project samples/HappyPath
```

## The one hard rule: dependency direction

Dependencies flow one way only:

```
samples/* → Infrastructure(.Anthropic | .AzureOpenAI | .Ollama | .Resilience | .Persistence) → Framework
```

`Framework` has a single external dependency (`Microsoft.Extensions.DependencyInjection.Abstractions`) and must stay that way — it is the stable core everything else builds on. Don't add a reference that points "up", and don't pull infrastructure into `Framework`.

## Conventions

The full set lives in [CLAUDE.md](CLAUDE.md) — SOLID, ports-and-adapters, primary constructors, interfaces over implementations, small focused changes, no speculative abstractions, and XML docs on the public API surface only. The short version:

- Keep the diff small and the change focused.
- Remove unused `using`s in every file you touch.
- No new dependency for something a few lines of standard library can do.
- **Docs travel with code.** If you rename a symbol, change a default, or add/remove a feature, update the README, CLAUDE.md, and `docs/*` in the same change so they never drift.

## Pull requests

Scope a PR to one change, make sure `build` and `test` are green, and describe the *why*, not just the *what*.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
