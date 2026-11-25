$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src/CodeAssist.Cli/CodeAssist.Cli.csproj'
& dotnet run --project $project --no-restore -v:q -- @args
exit $LASTEXITCODE