# RCA - Root Cause Analyzer

CLI tool for analyzing failed test logs using LLM providers.

Built with `Microsoft.Extensions.AI` for unified provider interface.

## Installation

```bash
dotnet tool install --global rca-cli
```

## Quick Start

```bash
# Set API key
export OPENAI_API_KEY=sk-...

# Analyze failed tests
rca analyze ./test-results/failed-tests.json
```

## Usage

```bash
rca analyze <path> [options]

Arguments:
  <path>    Path to JSON log file or directory

Options:
  --provider <provider>  LLM provider [default: openai]
                         Values: openai, claude, gemini, ollama
  --api-key <key>        API key (overrides env var)
  --model <model>        Model override
  --output <file>        Save report to file
```

## Providers

| Provider | Env Variable | Default Model |
|----------|--------------|---------------|
| openai   | `OPENAI_API_KEY` | gpt-4o |
| claude   | `ANTHROPIC_API_KEY` | claude-sonnet-4-20250514 |
| gemini   | `GEMINI_API_KEY` | gemini-1.5-flash |
| ollama   | - | llama3 |

## CI/CD Integration

### GitHub Actions

```yaml
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Run tests
        run: dotnet test --logger "json;LogFilePath=test-results.json"
        continue-on-error: true

      - name: Analyze failures
        if: failure()
        env:
          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
        run: |
          dotnet tool install --global Rca
          rca analyze ./test-results.json --output rca-report.md

      - name: Upload report
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: rca-report
          path: rca-report.md
```

### Azure DevOps

```yaml
- task: DotNetCoreCLI@2
  inputs:
    command: test
  continueOnError: true

- script: |
    dotnet tool install --global Rca
    rca analyze $(System.DefaultWorkingDirectory)/TestResults --output $(Build.ArtifactStagingDirectory)/rca-report.md
  condition: failed()
  env:
    ANTHROPIC_API_KEY: $(ANTHROPIC_API_KEY)
  displayName: 'RCA Analysis'

- publish: $(Build.ArtifactStagingDirectory)/rca-report.md
  artifact: rca-report
  condition: failed()
```

### GitLab CI

```yaml
test:
  stage: test
  script:
    - dotnet test
  allow_failure: true
  artifacts:
    paths:
      - TestResults/

rca:
  stage: report
  when: on_failure
  variables:
    OPENAI_API_KEY: $OPENAI_API_KEY
  script:
    - dotnet tool install --global Rca
    - rca analyze ./TestResults --output rca-report.md
  artifacts:
    paths:
      - rca-report.md
```

## Input Format

JSON array of failed tests (compatible with xUnitOTel):

```json
[
  {
    "testName": "CalculatorTests.Add_TwoNumbers_ReturnsSum",
    "className": "CalculatorTests",
    "errorMessage": "Assert.Equal() Failure\nExpected: 5\nActual:   4",
    "stackTrace": "at CalculatorTests.Add_TwoNumbers_ReturnsSum() in Tests.cs:line 15",
    "timestamp": "2024-01-15T10:30:00Z"
  }
]
```

## Output Example

```markdown
## Analysis

### CalculatorTests.Add_TwoNumbers_ReturnsSum

**Root Cause:** Off-by-one error in Add method
**Category:** Assertion failure
**Fix:** Check the Add implementation - likely returns `a + b - 1` instead of `a + b`

---

### Common Patterns
No common patterns detected - single isolated failure.
```

## Exit Codes

| Code | Description |
|------|-------------|
| 0    | Success |
| 1    | Error (missing logs, API failure, invalid config) |

## Local Development

```bash
git clone https://github.com/user/rca.git
cd rca
dotnet build
dotnet run --project src/Rca -- analyze ./samples/failed-tests.json
```

## License

MIT
