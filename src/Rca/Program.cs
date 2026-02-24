using System.CommandLine;
using Rca.Commands;

var root = new RootCommand("RCA - Root Cause Analyzer for failed tests");
root.Add(new AnalyzeCommand());

return await root.Parse(args).InvokeAsync();
